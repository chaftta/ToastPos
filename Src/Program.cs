using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Generic;

/// <summary>
/// Windows通知ウィンドウを自動的に画面右上に移動するシステムトレイ常駐アプリケーション
/// </summary>
class ToastMover {
	#region Windows API

	/// <summary>
	/// 指定されたクラス名とウィンドウ名を持つトップレベルウィンドウのハンドルを取得します
	/// </summary>
	[DllImport("user32.dll", SetLastError = true)]
	static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

	/// <summary>
	/// ウィンドウのサイズ、位置、Zオーダーを変更します
	/// </summary>
	[DllImport("user32.dll", SetLastError = true)]
	static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

	/// <summary>
	/// 指定されたウィンドウの外接する四角形の寸法を取得します
	/// </summary>
	[DllImport("user32.dll")]
	static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

	/// <summary>
	/// 指定されたウィンドウのクラス名を取得します
	/// </summary>
	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

	/// <summary>
	/// すべてのトップレベルウィンドウを列挙します
	/// </summary>
	[DllImport("user32.dll")]
	static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	/// <summary>
	/// EnumWindows関数のコールバックデリゲート
	/// </summary>
	delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	/// <summary>
	/// 指定されたウィンドウが可視状態かどうかを判定します
	/// </summary>
	[DllImport("user32.dll")]
	static extern bool IsWindowVisible(IntPtr hWnd);

	/// <summary>
	/// 指定されたウィンドウのタイトルバーのテキストを取得します
	/// </summary>
	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

	/// <summary>
	/// ウィンドウの位置とサイズを表す矩形構造体
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct RECT {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	#endregion

	#region 定数

	/// <summary>SetWindowPos: ウィンドウサイズを変更しない</summary>
	const uint SWP_NOSIZE = 0x0001;

	/// <summary>SetWindowPos: Zオーダーを変更しない</summary>
	const uint SWP_NOZORDER = 0x0004;

	/// <summary>SetWindowPos: ウィンドウを表示する</summary>
	const uint SWP_SHOWWINDOW = 0x0040;

	#endregion

	#region フィールド

	/// <summary>発見された通知ウィンドウのハンドル</summary>
	static IntPtr foundNotificationWindow = IntPtr.Zero;

	/// <summary>処理済みウィンドウのハンドルセット</summary>
	static HashSet<IntPtr> processedWindows = new HashSet<IntPtr>();

	/// <summary>ウィンドウの最後の位置を記録する辞書</summary>
	static Dictionary<IntPtr, RECT> lastWindowPositions = new Dictionary<IntPtr, RECT>();

	/// <summary>システムトレイアイコン</summary>
	static NotifyIcon trayIcon;

	/// <summary>監視ループの実行フラグ</summary>
	static bool isRunning = true;

	#endregion

	#region ヘルパーメソッド

	/// <summary>
	/// ウィンドウの位置またはサイズが変更されたかを判定します
	/// </summary>
	/// <param name="oldRect">以前の矩形</param>
	/// <param name="newRect">現在の矩形</param>
	/// <returns>変更された場合はtrue</returns>
	static bool HasWindowChanged(RECT oldRect, RECT newRect) {
		return oldRect.Left != newRect.Left || oldRect.Top != newRect.Top ||
			   oldRect.Right != newRect.Right || oldRect.Bottom != newRect.Bottom;
	}

	/// <summary>
	/// EnumWindows用のコールバック関数。通知ウィンドウを検索します
	/// </summary>
	/// <param name="hWnd">ウィンドウハンドル</param>
	/// <param name="lParam">アプリケーション定義の値</param>
	/// <returns>継続する場合はtrue、停止する場合はfalse</returns>
	static bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam) {
		if (!IsWindowVisible(hWnd))
			return true;

		System.Text.StringBuilder className = new System.Text.StringBuilder(256);
		GetClassName(hWnd, className, className.Capacity);
		string classNameStr = className.ToString();

		System.Text.StringBuilder windowTitle = new System.Text.StringBuilder(256);
		GetWindowText(hWnd, windowTitle, windowTitle.Capacity);
		string windowTitleStr = windowTitle.ToString();

		RECT rect;
		if (GetWindowRect(hWnd, out rect)) {
			int width = rect.Right - rect.Left;
			int height = rect.Bottom - rect.Top;

			// 「新しい通知」というタイトルのウィンドウを探す
			if (windowTitleStr == "新しい通知" || windowTitleStr == "New notification") {
				foundNotificationWindow = hWnd;
				return false; // 見つかったので列挙を停止
			}

			// サイズベースの検索（フォールバック）
			bool isToastCandidate =
				classNameStr.Contains("Toast") ||
				classNameStr.Contains("Notification") ||
				windowTitleStr.Contains("通知");

			if (isToastCandidate) {
				// 通知ウィンドウらしいサイズ（幅200-600px、高さ50-400px程度）
				if (width > 200 && width < 600 && height > 50 && height < 400) {
					foundNotificationWindow = hWnd;
					return false; // 見つかったので列挙を停止
				}
			}
		}

		return true; // 継続
	}

	/// <summary>
	/// システムトレイアイコンとコンテキストメニューを初期化します
	/// </summary>
	static void InitializeTrayIcon() {
		// システムトレイアイコンの作成
		trayIcon = new NotifyIcon();

		// 埋め込みリソースからアイコンを読み込む
		var assembly = System.Reflection.Assembly.GetExecutingAssembly();
		using (var stream = assembly.GetManifestResourceStream("ToastPos.icon.ico")) {
			if (stream != null) {
				trayIcon.Icon = new System.Drawing.Icon(stream);
			} else {
				// アイコンが見つからない場合はデフォルトアイコンを使用
				trayIcon.Icon = System.Drawing.SystemIcons.Information;
			}
		}

		trayIcon.Text = "通知位置移動プログラム";
		trayIcon.Visible = true;

		// コンテキストメニューの作成
		ContextMenuStrip contextMenu = new ContextMenuStrip();

		ToolStripMenuItem statusItem = new ToolStripMenuItem("通知監視中...");
		statusItem.Enabled = false;
		contextMenu.Items.Add(statusItem);

		contextMenu.Items.Add(new ToolStripSeparator());

		ToolStripMenuItem exitItem = new ToolStripMenuItem("終了");
		exitItem.Click += (sender, e) => {
			isRunning = false;
			trayIcon.Visible = false;
			trayIcon.Dispose();
			Application.Exit();
		};
		contextMenu.Items.Add(exitItem);

		trayIcon.ContextMenuStrip = contextMenu;
	}

	#endregion

	#region メインメソッド

	/// <summary>
	/// アプリケーションのエントリーポイント
	/// </summary>
	/// <param name="args">コマンドライン引数</param>
	[STAThread]
	static void Main(string[] args) {
		// システムトレイアイコンを初期化
		InitializeTrayIcon();

		// 画面の右上座標を計算
		int screenWidth = Screen.PrimaryScreen.Bounds.Width;
		int targetX = screenWidth - 10; // 右端から10ピクセル左
		int targetY = 10; // 上端から10ピクセル下

		int checkCount = 0;

		// バックグラウンドスレッドで監視を開始
		Thread monitorThread = new Thread(() => MonitorNotifications(screenWidth, targetX, targetY, ref checkCount));
		monitorThread.IsBackground = true;
		monitorThread.Start();

		// UIメッセージループを開始（トレイアイコンの動作に必要）
		Application.Run();
	}

	#endregion

	#region 通知処理メソッド

	/// <summary>
	/// 通知ウィンドウを処理し、必要に応じて画面右上に移動します
	/// </summary>
	/// <param name="hwnd">通知ウィンドウのハンドル</param>
	/// <param name="screenWidth">画面の幅</param>
	/// <param name="targetY">目標Y座標</param>
	static void ProcessNotificationWindow(IntPtr hwnd, int screenWidth, int targetY) {
		RECT rect;
		if (!GetWindowRect(hwnd, out rect))
			return;

		int width = rect.Right - rect.Left;
		int height = rect.Bottom - rect.Top;

		// 高さが0または異常値の場合はスキップ
		if (height <= 0 || height > 1000 || width <= 0 || width > 2000)
			return;

		// 位置/サイズが変更されたかチェック
		bool hasChanged = !lastWindowPositions.ContainsKey(hwnd) ||
						  HasWindowChanged(lastWindowPositions[hwnd], rect);

		// ウィンドウサイズを考慮して右上に配置
		int adjustedX = screenWidth - width - 10;

		// 既に右上付近にある場合はスキップ（移動済みと判断）
		bool isAtTarget = Math.Abs(rect.Left - adjustedX) < 50 && Math.Abs(rect.Top - targetY) < 50;

		// 状態が変化した、かつ右上にない場合のみ処理
		if (hasChanged && !isAtTarget) {
			Thread.Sleep(100);

			bool result = SetWindowPos(hwnd, IntPtr.Zero, adjustedX, targetY, 0, 0,
				SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);

			if (result) {
				RECT newRect;
				if (GetWindowRect(hwnd, out newRect)) {
					lastWindowPositions[hwnd] = newRect;
				}
			}
		} else {
			// 位置を記録（変化していないか、既に右上にある）
			lastWindowPositions[hwnd] = rect;
		}
	}

	/// <summary>
	/// 処理済みリストから、もう存在しないウィンドウを削除します
	/// </summary>
	static void CleanupInvisibleWindows() {
		var toRemove = new List<IntPtr>();
		foreach (var hwnd in processedWindows) {
			if (!IsWindowVisible(hwnd)) {
				toRemove.Add(hwnd);
			}
		}
		foreach (var hwnd in toRemove) {
			processedWindows.Remove(hwnd);
		}
	}

	/// <summary>
	/// 通知ウィンドウを常時監視し、新しい通知を右上に移動します
	/// </summary>
	/// <param name="screenWidth">画面の幅</param>
	/// <param name="targetX">目標X座標（未使用）</param>
	/// <param name="targetY">目標Y座標</param>
	/// <param name="checkCount">チェック回数のカウンター</param>
	static void MonitorNotifications(int screenWidth, int targetX, int targetY, ref int checkCount) {
		// 常時監視ループ
		while (isRunning) {
			try {
				checkCount++;

				// 検索結果をリセット
				foundNotificationWindow = IntPtr.Zero;

				// まずFindWindowで直接検索
				foundNotificationWindow = FindWindow(null, "新しい通知");

				// 見つからなければEnumWindowsで検索
				if (foundNotificationWindow == IntPtr.Zero) {
					EnumWindows(EnumWindowCallback, IntPtr.Zero);
				}

				// 処理済みリストから、もう存在しないウィンドウを削除
				CleanupInvisibleWindows();

				if (foundNotificationWindow != IntPtr.Zero) {
					ProcessNotificationWindow(foundNotificationWindow, screenWidth, targetY);
				}
			} catch (Exception) {
				// エラーは無視して監視を継続
			}

			// CPU負荷を軽減するための待機
			Thread.Sleep(500);
		}
	}

	#endregion
}
