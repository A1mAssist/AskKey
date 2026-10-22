# WinUI 3 自绘托盘菜单：圆角、白边、尺寸与图标对齐

适用：WinUI 3 / Windows App SDK，用 XAML 内容 + 原生 `SetWindowPos` 自绘托盘右键菜单，不使用系统 `MenuFlyout`。

四个问题按根因排序，每个都给出**为什么**和**怎么验证**。最后一节是最容易被前面几个带偏的死结。

---

## 0. 整体结构

托盘菜单不是 `MenuFlyout`，是一个**普通 `Window`**，里面放一个 `Border` 卡片：

- 窗口带 `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`（透明、不抢焦点、不进任务栏）
- 卡片圆角靠 `SetWindowRgn` 裁窗口区域实现，**不是**靠 XAML 的 `CornerRadius`
- 卡片背景自己画（`Border Background`），窗口本身必须透明
- 定位用原生 `SetWindowPos`，坐标是**物理像素**

因此有三个独立的层会互相影响：**窗口尺寸 → 窗口圆角区域 → 卡片内容**。绝大多数"看起来不对"都是这三层之一算错了。

---

## 1. 去掉 DWM 灰边 / 白边

### 症状
菜单外围有一圈 1px 灰边或白边，四个角尤其明显。

### 原因
WinUI 窗口默认带 DWM 边框。虽然 `SetBorderAndTitleBar(false, false)` 隐藏了标题栏，但 DWM 仍在窗口外缘画一圈自己的边框——它画在 XAML 内容之外，XAML 的 `CornerRadius` 管不到。

### 做法
对承载菜单的窗口显式关掉 DWM 边框，并把圆角交给 `SetWindowRgn`：

```csharp
private const int DwmWindowCornerPreference = 33;
private const int DwmWindowCornerDoNotRound = 1;   // 2 = ROUND, 1 = DONOTROUND
private const int DwmBorderColor = 34;
private const int DwmBorderColorNone = unchecked((int)0xFFFFFFFE);

public static void DisableWindowFrame(Window window)
{
    var handle = WindowNative.GetWindowHandle(window);
    var border = DwmBorderColorNone;              // DWMWA_BORDER_COLOR = 0xFFFFFFFE 表示"无"
    DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
    var corners = DwmWindowCornerDoNotRound;      // 圆角我自己用 SetWindowRgn 做
    DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corners, sizeof(int));
}
```

在构造函数里 `InitializeComponent()` 之后立刻调用。

### 关键点
`0xFFFFFFFE` 这个魔数是 `DWMWA_COLOR_NONE`，不是 `COLOR_DEFAULT`。写成 `0` 会得到黑色边框。

---

## 2. 尺寸：三个必须避开的坑

这一节是全文最重要的部分。这三个坑都踩过，而且互相掩盖，导致反复修不好。

### 坑 1：不要用 `DesiredSize` 决定窗口尺寸

#### 症状
窗口高度逐次变大，最后变成一条贯穿屏幕的黑条。

#### 原因
`MenuRoot` 是铺满客户区的 `Grid`，`Border` 默认 `VerticalAlignment="Stretch"`。用无限边界 `Measure` 这些元素，`DesiredSize.Height` 返回的是**上一次窗口的高度**，再喂回下一次尺寸计算，形成自反馈循环，窗口一路涨到工作区高度。

#### 写法陷阱
`VerticalAlignment="Top"` **只控制排列，不改变 `DesiredSize`**。我一度以为设了它就能量出内容高度，实测证明不行。换测量对象（`MenuRoot` → `Border` → `StackPanel`）全都无效，因为前两个都是拉伸元素。

#### 正确做法
行数固定，直接**算**：

```csharp
private const double MinPanelWidth = 152;
// 5 个按钮 @ (30 高 + 1 上边距 + 1 下边距)
// + 2 个分隔线 @ (1 高 + 2 上 + 2 下)
// + 卡片上下内边距 4 + 4
private const double PanelContentHeightDip = 5 * 32 + 2 * 5 + 8;

var desired = new Size(MinPanelWidth, PanelContentHeightDip);
```

加减菜单行或改行高时，**必须同步这个常数**，并在注释里写清构成。

### 坑 2：不要用 `GetSystemMetrics` 取窗口边框 ← 本次真正的元凶

#### 症状
高度算出来是几万 px，被 Windows 钳到工作区高度，表现为贯穿屏幕的黑条。**调内边距、换测量对象都完全无效。**

#### 原因
用了这个 API 补边框：

```csharp
// 错误
var frameX = GetSystemMetrics(SmCxFrame) + GetSystemMetrics(SmCxPaddedBorder);
var frameY = GetSystemMetrics(SmCyFrame) + GetSystemMetrics(SmCyPaddedBorder);
```

实测这台机器返回 **11 和 33549**：

```
frame=11/33549 computed=239x33816 win=239x1626
```

`SM_CXPADDEDBORDER` / `SM_CYPADDEDBORDER` 按文档返回 `SM_CXFRAME` 的 **2 倍**，但对这类 WinUI 窗口返回的值不可靠。高度那个垃圾值直接把窗口撑爆。

#### 正确做法
**实测**。窗口尺寸减客户区尺寸就是边框：

```csharp
GetWindowRect(_windowHandle, out var frameWindow);
GetClientRect(_windowHandle, out var frameClient);
var frameX = Math.Max(0, (frameWindow.Right - frameWindow.Left) -
                        (frameClient.Right - frameClient.Left));
var frameY = Math.Max(0, (frameWindow.Bottom - frameWindow.Top) -
                        (frameClient.Bottom - frameClient.Top));

var width  = Math.Max(1, (int)Math.Ceiling(desired.Width  * scale) + frameX);
var height = Math.Max(1, (int)Math.Ceiling(desired.Height * scale) + frameY);
```

### 为什么必须补这个边框
`SetWindowPos` 收的是**外框矩形**，而客户区比它小。不补的话卡片右侧被裁（悬停高亮块右留白比左小），底部被裁（最后一项的圆角被削平）。

### 坑 3：`scale` 只能乘一次

`DesiredSize` 和常量都是 DIP，`SetWindowPos` 要物理像素，转换只能一次。之前写成 `desired.Width * scale` 之外又叠了一次边框换算，量纲混掉，窗口直接变整屏。

### 完整尺寸代码

```csharp
var scale = MenuRoot.XamlRoot?.RasterizationScale ?? GetDpiForWindow(_windowHandle) / 96d;
if (scale <= 0) scale = 1;
var desired = new Size(MinPanelWidth, PanelContentHeightDip);

GetWindowRect(_windowHandle, out var fw);
GetClientRect(_windowHandle, out var fc);
var frameX = Math.Max(0, (fw.Right - fw.Left) - (fc.Right - fc.Left));
var frameY = Math.Max(0, (fw.Bottom - fw.Top) - (fc.Bottom - fc.Top));

var width  = Math.Max(1, (int)Math.Ceiling(desired.Width  * scale) + frameX);
var height = Math.Max(1, (int)Math.Ceiling(desired.Height * scale) + frameY);
```

### DPI 与多显示器
- `scale` 每次弹出实时读 `RasterizationScale`，改系统缩放比例立即生效
- 工作区用 `DisplayArea.GetFromPoint(鼠标位置)`，菜单自动贴你右键的那块屏
- 唯一已知边缘情况：`WS_EX_LAYERED` 窗口跨不同 DPI 的屏幕移动时，`RasterizationScale` 可能短暂返回旧屏值。这是框架层行为，下次弹出通常恢复

实测验证值（本机 150%，DPI 144）：窗口 234 x 273，客户区 228 x 267，边框 6 px（3 px/边）。152 x 1.5 + 6 = 234，178 x 1.5 + 6 = 273。**尺寸完全由缩放比和实测边框决定，不含任何写死的像素值。**

---

## 3. 窗口圆角：`SetWindowRgn`

### 做法
窗口尺寸定下来后，用圆角矩形区域裁窗口：

```csharp
private const int CornerRadius = 8;   // DIP

private void ApplyRoundedRegion(int width, int height, double scale)
{
    GetWindowRect(_windowHandle, out var window);
    GetClientRect(_windowHandle, out var client);
    var origin = new NativePoint();
    ClientToScreen(_windowHandle, ref origin);
    var left = Math.Max(0, origin.X - window.Left);
    var top = Math.Max(0, origin.Y - window.Top);
    var clientWidth = Math.Max(0, client.Right - client.Left);
    var clientHeight = Math.Max(0, client.Bottom - client.Top);
    var radius = Math.Max(1, (int)Math.Round(CornerRadius * scale));

    // 注意：最后两个参数是圆角椭圆的【宽高】，不是半径。
    // 传 radius*2 恰好正确（半径 x 2 = 椭圆宽）。
    var region = CreateRoundRectRgn(
        left, top, left + clientWidth + 1, top + clientHeight + 1, radius * 2, radius * 2);
    if (region != IntPtr.Zero && SetWindowRgn(_windowHandle, region, true) == 0)
        DeleteObject(region);
}
```

### 三个要点
1. **`CreateRoundRectRgn` 最后两个参数是椭圆宽高，不是半径。** 容易看错。
2. **要同时设 `WS_EX_LAYERED` + `SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA)`**，否则窗口不透明，卡片圆角外会露出方形底色。
3. **显示顺序**：先把 alpha 设成 0（全透明），`SetWindowPos` 定尺寸，`SetWindowRgn` 裁圆角，最后 alpha 设回 255。一次闪现都不要有。

### 动画
想加弹出动画，用 `DispatcherQueueTimer` 驱动 `Opacity` + `Translation`，**不要**碰 `SetWindowRgn` 和 alpha 的时序。首次弹出先做一次离屏预热不播动画，之后正常播。

---

## 4. 图标对齐：`PathIcon` 与 `FontIcon` 不对齐

### 症状
同一个菜单里，某个 `PathIcon` 图标比旁边的 `FontIcon` 明显小一圈，显得又小又往下坠。

### 原因
`FontIcon`（Segoe Fluent Icons）在给定字号下本身就是光学尺寸对齐的。而 `PathIcon` 走自己的路径坐标系，24x24 画布里墨迹可能只占 15x14 个单位——缩到 15 DIP 后实际只有约 9.4 DIP 见方，而 `FontIcon` 接近满 15 DIP。

**关键点：这跟"被裁切"是两回事。** 在这上面浪费了好几轮，逐像素量过才发现笔画根本没被裁，纯粹是尺寸小 + 重心偏上。

### 修法：不改路径数据，只加变换

```xml
<!-- 24x24 路径下方留了约 7 个单位的空白画布，导致字形比旁边的 Fluent Icons 小、
     重心偏上。以墨迹中心（24 坐标系里的 10.5, 10）为原点放大并上移，
     不触碰原始路径数据。 -->
<Viewbox Width="15" Height="15"
         HorizontalAlignment="Left"
         VerticalAlignment="Center"
         RenderTransformOrigin="0.4385,0.4167">
  <Viewbox.RenderTransform>
    <TransformGroup>
      <ScaleTransform ScaleX="1.25" ScaleY="1.25" />
      <TranslateTransform Y="-1.1" />
    </TransformGroup>
  </Viewbox.RenderTransform>
  <PathIcon Data="M12.5,17 C..." />
</Viewbox>
```

- `RenderTransformOrigin` 用**墨迹中心 / 24**，不是画布中心
- 放大倍数和 `TranslateTransform` 的 Y 是仅有的两个可调项
- 需要按同样的方法缩放时，两项一起改，保持重心不变

### 如果允许换图标
用 `FontIcon` 换成 Segoe Fluent Icons 里对应的码位，尺寸和光学对齐自动与其他项一致。但如果你要求保留原图形，就用上面的变换方案。

---

## 5. 悬停高亮块与卡片内边距

### 对称性
卡片 `Padding` 和按钮 `Margin` 必须**上下左右各自对称**。曾经底部 4 DIP、顶部 2 DIP，导致悬停块上下两个角的弧度看起来不一致。

建议值：

```xml
<Border x:Name="MenuPanel"
        Background="{ThemeResource SurfaceBrush}"
        BorderThickness="0"
        CornerRadius="8"
        Padding="0,4,0,4"
        VerticalAlignment="Top">     <!-- 见第 2 节 -->
```

```xml
<Style x:Key="TrayMenuButtonStyle" TargetType="Button">
  <Setter Property="Height" Value="30" />
  <Setter Property="Margin" Value="3,1,3,1" />
  <Setter Property="Padding" Value="6,3,5,4" />
  <Setter Property="CornerRadius" Value="4" />
  ...
</Style>
```

### 悬停块的圆角要能画完
按钮悬停 `Border` 的 `CornerRadius` 需要足够的垂直空间走完弧。半径 4 DIP 在 150% 下是 6 px，卡片底部内边距 + 按钮下边距至少要 >= 4 DIP，否则底部两角被削平。给到 5 DIP 比较稳妥（4 DIP 画弧 + 1 DIP 可见缓冲）。

### 卡片主题色
深色下 `#23262A`，浅色下纯白 `#FFFFFF`，由 `ApplyTheme` 直接给 `MenuPanel.Background` 赋值，而不是写在 XAML 里——这样切换主题不需要重新加载 XAML。

---

## 6. 排查方法（这部分最重要）

上面四个问题全部先猜错了方向，靠看截图调数值，来回改了十几轮。**正确做法是读运行中窗口的真实数据。**

### 6.1 一次性日志诊断
在 `SetWindowPos` 前后各读一次矩形，写进日志：

```csharp
var movedOk = SetWindowPos(_windowHandle, HwndTopmost, left, top, width, height, flags);
GetWindowRect(_windowHandle, out var probeRect);
GetClientRect(_windowHandle, out var probeClient);
File.AppendAllText(
    Path.Combine(AppContext.BaseDirectory, "traymenu-diag.log"),
    $"scale={scale:F3} desired={desired.Width:F0}x{desired.Height:F0} frame={frameX}/{frameY} " +
    $"computed={width}x{height} left/top={left},{top} ok={movedOk} " +
    $"win={probeRect.Right - probeRect.Left}x{probeRect.Bottom - probeRect.Top} " +
    $"client={probeClient.Right}x{probeClient.Bottom}" + Environment.NewLine);
```

跑一次，读日志。这一行日志直接指出了 `frame=11/33549`，一步定位到真正的元凶。**排查完记得删掉。**

### 6.2 读运行中进程的窗口矩形
不用截图、不用抢焦点，直接问窗口。用 PowerShell 加一段 `Add-Type`，用 `EnumWindows` 找到标题为 `Ask Key` 的窗口，打印 `GetWindowRect` 和 `GetClientRect` 的尺寸和位置。

关键 P/Invoke 签名：

```csharp
[DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
```

窗口隐藏时会显示 1x1 或 6x6 的初始尺寸——**这不是 bug**，菜单是点击时才 `SetWindowPos` 的。要量真实尺寸需要先打开菜单。

### 6.3 主动验证，不要拿公式当证据
对运行中的窗口直接 `SetWindowPos` 设成你认为正确的尺寸，看它稳不稳：

- 设完就弹回去 → 有东西在覆盖，查 `AppWindow` / `Resize`
- 稳住不变 → 调用方算错了，去看它到底传了什么

这条能一次区分两种完全不同的情况。本次实测：设成 239 x 273 后稳在 273，没弹回去 → 说明窗口接受正确尺寸，问题只在传进去的数字。

### 6.4 像素测量的正确用法
截图只能告诉你"看起来不对称"，不能告诉你原因。真要量就按颜色分类计数：逐行找面板色（如 `(35,38,42)`）和悬停色（如 `(48,51,55)`）的 x 范围，比较左右留白。

**但**：量出"不对称"之后，必须回到代码找不对称的那一行。像素只能验证假设，不能产生假设。

---

## 7. 教训

1. **不要用 `GetSystemMetrics(SM_CXPADDEDBORDER / SM_CYPADDEDBORDER)` 取窗口边框。** 对 WinUI 窗口返回垃圾值（实测 11 / 33549）。用 `GetWindowRect - GetClientRect` 实测。
2. **不要用 `DesiredSize` 给自绘浮窗定尺寸。** 拉伸元素返回的是上一次窗口尺寸，会自反馈成死循环。用固定行数算。
3. **`VerticalAlignment` 不改变 `DesiredSize`。** 它只控制排列。
4. **`CreateRoundRectRgn` 最后两个参数是椭圆宽高，不是半径。**
5. **`DWMWA_BORDER_COLOR` 的"无"是 `0xFFFFFFFE`，不是 `0`。**
6. **`PathIcon` 的墨迹中心和画布中心通常差 2 个单位**，和 `FontIcon` 混排时要手动对齐。
7. **像素测量只能验证假设，不能产生假设。** 读运行中窗口的真实矩形。
8. **拿公式算出来的数字当验证，等于没验证。** 实测窗口矩形才算。
9. **改完必须真跑一次构建 + 部署 + 启动**，只编译不算数。
10. **窗口隐藏时读到 1x1 是正常的**，别当成 bug 去追。

---

## 8. 部署提示

运行目录必须来自 `dotnet build` 的输出，**不要**用 `dotnet publish` 的路径——publish 会丢 `App.xbf`、`Theme.xbf`、`Views/`、`Helpers/`，导致 `XAML parsing failed`：

```powershell
dotnet build .\windows\YourApp.WinUI\YourApp.WinUI.csproj -c Release -p:Platform=x64 -p:YourAppUnsigned=true
dotnet test  .\windows\YourApp.slnx -c Release --no-build

# 完整复制 build 输出到独立目录，再从那里启动
$src = '.\windows\YourApp.WinUI\bin\x64\Release\net10.0-windows10.0.26100.0'
$dst = 'D:\Tools\Temp\YourApp-run-<tag>'
Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
Start-Process -FilePath (Join-Path $dst 'YourApp.exe') -WorkingDirectory $dst -WindowStyle Hidden
```

核对复制后的文件数（自包含 WinUI 约 1286 个文件），并确认 `App.xbf`、主窗口 `.xbf`、`Views/`、`Helpers/` 都在。启动后记录 PID 报给用户。