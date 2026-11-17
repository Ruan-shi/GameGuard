<#
    GameGuard - 游戏时长守卫（影之刃零 / Steam）
    单次游玩 60 分钟触发提醒，可结束游戏或续期 5 分钟，最多续期 2 次。
#>

param(
    [switch]$SelfTest,
    [switch]$ScheduleTest,
    [switch]$UiTest,
    [switch]$GamesTest,
    [switch]$PanelTest
)

# ==================== 配置区 ====================
# ---- 受管控的游戏列表（可在托盘「管理游戏列表」中增删）----
$Games = @(
    [pscustomobject]@{ Name = '影之刃零'; Processes = @('PBZ', '8za_v2'); PathFilter = @('PBZ') }
)
$PlayMinutes      = 60                   # 单次游玩时长（分钟）
$ExtendMinutes    = 5                    # 每次续期时长（分钟）
$MaxExtend        = 2                    # 续期次数上限
$PopupTimeoutSec  = 60                   # 弹窗无操作自动结束倒计时（秒）
$PollSeconds      = 15                   # 进程轮询间隔（秒）
$GameTitle        = '影之刃零'

# ---- 固定时段管控 ----
$UseSchedule      = $true               # $true: 启用固定时段模式（此时 $PlayMinutes 连续时长规则自动暂停）
$ScheduleStart    = '18:00'             # 允许开始时间
$ScheduleEnd      = '20:00'             # 必须结束时间
$ScheduleWarnMin  = 3                   # 结束前多少分钟弹出预警
$ScheduleDays     = @(0,1,2,3,4,5,6)    # 生效星期：0=周日 1=周一 … 6=周六；@() 表示每天
$OutsideCountdown = 5                   # 时段外启动游戏时，提示后多少秒关闭
$ScheduleWithContinuous = $true         # 时段内是否同时应用连续时长+续期规则（$true=叠加）
$GameTitle        = '游戏'               # 仅在无法识别具体游戏时使用的通用称呼

# 临时调整（仅当天生效，次日自动失效）
$TempDate         = ''
$TempStart        = ''
$TempEnd          = ''
# ================================================

$ErrorActionPreference = 'Stop'
if ($MyInvocation.MyCommand.Path) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    # PS2EXE 编译后的 exe 环境：取 exe 自身所在目录
    $ScriptDir = Split-Path -Parent ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
}
$LogPath   = Join-Path $ScriptDir 'gameguard.log'
$PauseFlag = Join-Path $ScriptDir 'paused.flag'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 声明 DPI 感知，避免高缩放屏下弹窗位置与坐标错位
if (-not ('DpiNative' -as [type])) {
    Add-Type -TypeDefinition 'public class DpiNative { [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
}
[void][DpiNative]::SetProcessDPIAware()

if (-not ('Win32Native' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Native {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@
}

function Write-Log {
    param([string]$Message)
    $line = '{0}  {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message
    try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 } catch { }
}

function Get-GameProcesses {
    $found = @()
    foreach ($g in $Games) {
        $procs = @()
        foreach ($pn in @($g.Processes)) {
            if (-not $pn) { continue }
            $ps = Get-Process -Name $pn -ErrorAction SilentlyContinue
            if ($ps) { $procs += @($ps) }
        }
        if ($procs.Count -gt 0) {
            $filters = @($g.PathFilter | Where-Object { $_ })
            if ($filters.Count -gt 0) {
                $procs = @($procs | Where-Object {
                    $fp = $null
                    try { $fp = $_.Path } catch { $fp = $null }
                    $ok = $false
                    if ($fp) {
                        foreach ($frag in $filters) {
                            if ($fp -like "*$frag*") { $ok = $true; break }
                        }
                    }
                    $ok
                })
            }
        }
        $found += $procs
    }
    return @($found)
}

function Get-ActiveGame {
    foreach ($g in $Games) {
        foreach ($pn in @($g.Processes)) {
            if (-not $pn) { continue }
            if (Get-Process -Name $pn -ErrorAction SilentlyContinue) { return $g }
        }
    }
    return $null
}

function Test-FullScreenWindow {
    param([IntPtr]$Handle)
    if ($Handle -eq [IntPtr]::Zero) { return $false }
    $rect = New-Object Win32Native+RECT
    $ok = [Win32Native]::GetWindowRect($Handle, [ref]$rect)
    if (-not $ok) { return $false }
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    return (($w -ge ($screen.Width - 8)) -and ($h -ge ($screen.Height - 8)))
}

# ==================== 统一界面组件（所有窗口共用）====================
# 配色：红为主色（与盾牌图标一致），灰阶为辅助
$script:C_Primary      = [System.Drawing.Color]::FromArgb(198, 40, 40)
$script:C_PrimaryHover = [System.Drawing.Color]::FromArgb(170, 32, 32)
$script:C_PrimaryDown  = [System.Drawing.Color]::FromArgb(144, 26, 26)
$script:C_Text         = [System.Drawing.Color]::FromArgb(38, 38, 38)
$script:C_Sub          = [System.Drawing.Color]::FromArgb(132, 132, 132)
$script:C_Line         = [System.Drawing.Color]::FromArgb(228, 228, 228)
$script:C_BtnBg        = [System.Drawing.Color]::FromArgb(246, 246, 246)
$script:C_BtnHover     = [System.Drawing.Color]::FromArgb(234, 234, 234)
$script:C_BtnDown      = [System.Drawing.Color]::FromArgb(222, 222, 222)
$script:C_Border       = [System.Drawing.Color]::FromArgb(206, 206, 206)

# 按系统 DPI 计算缩放系数（96 DPI = 1.0）：所有尺寸以 96 DPI 为基准书写，
# 在 125% / 150% 等高缩放下自动放大，避免文字被切、控件重叠。
$script:uiScale = 1.0
try {
    $gDpi = [System.Drawing.Graphics]::FromHwnd([System.IntPtr]::Zero)
    if ($gDpi) {
        $d = $gDpi.DpiX / 96.0
        if ($d -ge 1.0) { $script:uiScale = $d }
        $gDpi.Dispose()
    }
} catch { }
if ($script:uiScale -gt 3.0) { $script:uiScale = 3.0 }

function U {
    param([double]$Value = 0)
    return [int][math]::Round($Value * $script:uiScale)
}

function New-UiForm {
    param(
        [string]$Title = '',
        [int]$W = 400,
        [int]$H = 300,
        [switch]$NoControlBox
    )
    $f = New-Object System.Windows.Forms.Form
    $f.Text = $Title
    $f.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
    $f.ClientSize = New-Object System.Drawing.Size((U $W), (U $H))
    $f.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $f.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $f.MaximizeBox = $false
    $f.MinimizeBox = $false
    $f.ShowInTaskbar = $false
    if ($NoControlBox) { $f.ControlBox = $false }
    $f.TopMost = $true
    $f.BackColor = [System.Drawing.Color]::White
    $f.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    try { $f.Icon = New-GuardIcon } catch { }
    return $f
}

function New-UiText {
    param(
        [string]$Text = '',
        [int]$X = 0,
        [int]$Y = 0,
        [int]$W = 100,
        [int]$H = 26,
        [double]$Size = 10,
        [switch]$Bold,
        [System.Drawing.Color]$Color = [System.Drawing.Color]::Black,
        [string]$Align = 'MiddleLeft'
    )
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $Text
    $style = [System.Drawing.FontStyle]::Regular
    if ($Bold) { $style = [System.Drawing.FontStyle]::Bold }
    $l.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', $Size, $style)
    $l.ForeColor = $Color
    $l.BackColor = [System.Drawing.Color]::Transparent
    $l.AutoSize = $false
    $l.UseMnemonic = $false
    $l.TextAlign = $Align
    $l.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U $H))
    return $l
}

function New-UiBtn {
    param(
        [string]$Text = '',
        [int]$X = 0,
        [int]$Y = 0,
        [int]$W = 100,
        [int]$H = 36,
        [switch]$Primary,
        [double]$Size = 10
    )
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text
    $b.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', $Size, [System.Drawing.FontStyle]::Regular)
    $b.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U $H))
    $b.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $b.Cursor = [System.Windows.Forms.Cursors]::Hand
    $b.UseVisualStyleBackColor = $false
    if ($Primary) {
        $b.BackColor = $script:C_Primary
        $b.ForeColor = [System.Drawing.Color]::White
        $b.FlatAppearance.BorderSize = 0
        $b.FlatAppearance.MouseOverBackColor = $script:C_PrimaryHover
        $b.FlatAppearance.MouseDownBackColor = $script:C_PrimaryDown
    } else {
        $b.BackColor = $script:C_BtnBg
        $b.ForeColor = $script:C_Text
        $b.FlatAppearance.BorderSize = 1
        $b.FlatAppearance.BorderColor = $script:C_Border
        $b.FlatAppearance.MouseOverBackColor = $script:C_BtnHover
        $b.FlatAppearance.MouseDownBackColor = $script:C_BtnDown
    }
    return $b
}

function New-UiLine {
    param([int]$X = 0, [int]$Y = 0, [int]$W = 200, [int]$H = 1)
    $p = New-Object System.Windows.Forms.Panel
    $p.BackColor = $script:C_Line
    $h = U $H
    if ($h -lt 1) { $h = 1 }
    $p.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), $h)
    return $p
}

function Add-UiTopBar {
    param([System.Windows.Forms.Form]$Form, [int]$W = 400)
    $p = New-Object System.Windows.Forms.Panel
    $p.BackColor = $script:C_Primary
    $p.Bounds = New-Object System.Drawing.Rectangle(0, 0, (U $W), (U 6))
    $Form.Controls.Add($p)
}

function New-UiTimePicker {
    param([int]$X = 0, [int]$Y = 0, [int]$W = 104, [string]$Value = '00:00')
    $d = New-Object System.Windows.Forms.DateTimePicker
    $d.Format = [System.Windows.Forms.DateTimePickerFormat]::Custom
    $d.CustomFormat = 'HH:mm'
    $d.ShowUpDown = $true
    $d.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $d.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U 30))
    try { $d.Value = [datetime]::ParseExact($Value, 'HH:mm', $null) } catch { }
    return $d
}

function New-UiNumber {
    param([int]$X = 0, [int]$Y = 0, [int]$W = 74, [int]$Min = 0, [int]$Max = 999, [int]$Value = 0)
    $n = New-Object System.Windows.Forms.NumericUpDown
    $n.Minimum = $Min
    $n.Maximum = $Max
    if ($Value -lt $Min) { $Value = $Min }
    if ($Value -gt $Max) { $Value = $Max }
    $n.Value = $Value
    $n.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $n.TextAlign = [System.Windows.Forms.HorizontalAlignment]::Center
    $n.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U 30))
    return $n
}

function New-UiRadio {
    param([string]$Text = '', [int]$X = 0, [int]$Y = 0, [int]$W = 120, [int]$H = 28, [bool]$Checked = $false)
    $r = New-Object System.Windows.Forms.RadioButton
    $r.Text = $Text
    $r.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $r.BackColor = [System.Drawing.Color]::Transparent
    $r.UseVisualStyleBackColor = $true
    $r.AutoSize = $false
    $r.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U $H))
    $r.Checked = $Checked
    return $r
}

function New-UiCheck {    param([string]$Text = '', [int]$X = 0, [int]$Y = 0, [int]$W = 200, [int]$H = 30, [bool]$Checked = $false)
    $c = New-Object System.Windows.Forms.CheckBox
    $c.Text = $Text
    $c.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    $c.BackColor = [System.Drawing.Color]::Transparent
    $c.UseVisualStyleBackColor = $true
    $c.AutoSize = $false
    $c.Bounds = New-Object System.Drawing.Rectangle((U $X), (U $Y), (U $W), (U $H))
    $c.Checked = $Checked
    return $c
}

function Show-GuardDialog {
    param(
        [int]$PlayedMinutes,
        [int]$RemainExtend,
        [int]$TimeoutSeconds = $PopupTimeoutSec,
        [string]$CapturePath = '',
        [string]$GameName = $GameTitle
    )

    $script:dialogChoice = 'timeout'
    $script:dialogRemain = $TimeoutSeconds

    $form = New-UiForm -Title '游戏时间提醒' -W 380 -H 204 -NoControlBox
    Add-UiTopBar -Form $form -W 380

    $lblTitle = New-UiText -Text ('已经玩了 {0} 分钟' -f $PlayedMinutes) -X 20 -Y 26 -W 340 -H 46 -Size 20 -Bold -Color $script:C_Primary -Align 'MiddleCenter'

    $lblDesc = New-UiText -Text ('{0}还开着。要现在收手吗？' -f $GameName) -X 20 -Y 80 -W 340 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleCenter'

    $lblCount = New-UiText -Text ('未操作将在 {0} 秒后自动结束游戏' -f $script:dialogRemain) -X 20 -Y 108 -W 340 -H 22 -Size 9 -Color $script:C_Sub -Align 'MiddleCenter'

    $line1 = New-UiLine -X 20 -Y 138 -W 340

    $btnKill = New-UiBtn -Text '结束游戏' -X 20 -Y 150 -W 152 -H 40 -Primary -Size 11

    $btnExtend = New-UiBtn -Text '' -X 208 -Y 150 -W 152 -H 40 -Size 9

    if ($RemainExtend -gt 0) {
        $btnExtend.Text = ('再玩 {0} 分钟（剩 {1} 次）' -f $ExtendMinutes, $RemainExtend)
        $btnExtend.Enabled = $true
    } else {
        $btnExtend.Text = '续期次数已用完'
        $btnExtend.Enabled = $false
        $btnExtend.ForeColor = [System.Drawing.Color]::FromArgb(170, 170, 170)
    }

    $form.Controls.AddRange(@($lblTitle, $lblDesc, $lblCount, $line1, $btnKill, $btnExtend))

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 1000

    $script:gForm  = $form
    $script:gTimer = $timer
    $script:gLabel = $lblCount

    $btnKill.Add_Click({
        $script:dialogChoice = 'kill'
        $script:gTimer.Stop()
        $script:gForm.Close()
    })

    $btnExtend.Add_Click({
        $script:dialogChoice = 'extend'
        $script:gTimer.Stop()
        $script:gForm.Close()
    })

    $timer.Add_Tick({
        $script:dialogRemain--
        if ($script:dialogRemain -le 0) {
            $script:dialogChoice = 'timeout'
            $script:gTimer.Stop()
            $script:gForm.Close()
        } else {
            $script:gLabel.Text = ('未操作将在 {0} 秒后自动结束游戏' -f $script:dialogRemain)
        }
    })

    $form.Add_Shown({
        $script:gTimer.Start()
        try { [System.Media.SystemSounds]::Exclamation.Play() } catch { }
    })

    $form.Add_FormClosed({
        $script:gTimer.Stop()
        $script:gTimer.Dispose()
    })

    # 自测模式：定时截图，便于确认弹窗实际渲染效果
    if ($CapturePath) {
        $script:capPath = $CapturePath
        $cap = New-Object System.Windows.Forms.Timer
        $cap.Interval = 1500
        $cap.Add_Tick({
            $script:capTimer.Stop()
            try {
                $b = $script:gForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:capPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose()
                $bmp.Dispose()
            } catch { }
        })
        $script:capTimer = $cap
        $cap.Start()
    }

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:dialogChoice
}

function Invoke-Curfew {
    $procs = @(Get-GameProcesses)
    if ($procs.Count -eq 0) {
        $script:dueTime    = $null
        $script:roundStart = $null
        $script:extendUsed = 0
        return
    }

    $main = $procs | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    $wasMinimized = $false
    if ($main -and (Test-FullScreenWindow -Handle $main.MainWindowHandle)) {
        # 全屏独占会把普通置顶窗口压在下面，先最小化游戏确保提示可见
        [void][Win32Native]::ShowWindow($main.MainWindowHandle, 6)
        $wasMinimized = $true
        Start-Sleep -Milliseconds 800
    }

    $played = 0
    if ($script:roundStart) { $played = [int][Math]::Floor(((Get-Date) - $script:roundStart).TotalMinutes) }
    $remainExtend = [Math]::Max(0, $MaxExtend - $script:extendUsed)

    $activeGame = Get-ActiveGame
    $gname = if ($activeGame) { $activeGame.Name } else { $GameTitle }

    Write-Log ('弹出提醒：{0} 已玩 {1} 分钟，剩余续期 {2} 次' -f $gname, $played, $remainExtend)
    $choice = Show-GuardDialog -PlayedMinutes $played -RemainExtend $remainExtend -GameName $gname

    if ($choice -eq 'extend') {
        $script:extendUsed++
        $script:dueTime = (Get-Date).AddMinutes($ExtendMinutes)
        $left = [Math]::Max(0, $MaxExtend - $script:extendUsed)
        Write-Log ('用户续期 {0} 分钟，剩余 {1} 次，新截止 {2}' -f $ExtendMinutes, $left, $script:dueTime.ToString('HH:mm:ss'))
        if ($wasMinimized -and $main) {
            [void][Win32Native]::ShowWindow($main.MainWindowHandle, 9)
            [void][Win32Native]::SetForegroundWindow($main.MainWindowHandle)
        }
    }
    else {
        Write-Log ('结束游戏，原因：{0}' -f $(if ($choice -eq 'kill') { '用户选择' } else { '弹窗超时未响应' }))
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        $script:dueTime    = $null
        $script:roundStart = $null
        $script:extendUsed = 0
    }
}

function Get-ScheduleState {
    param()
    $now = Get-Date
    $out = [pscustomobject]@{ State = 'Disabled'; Start = $null; End = $null; Remain = $null }
    if (-not $UseSchedule) { return $out }

    # 今日临时调整优先，且不受星期限制
    $useStart = $ScheduleStart
    $useEnd   = $ScheduleEnd
    $dayOk = ($ScheduleDays.Count -eq 0) -or ($ScheduleDays -contains [int]$now.DayOfWeek)
    if ($TempDate -and $TempDate -eq $now.ToString('yyyy-MM-dd') -and $TempStart -and $TempEnd) {
        $useStart = $TempStart
        $useEnd   = $TempEnd
        $dayOk    = $true
    }

    $todayStart = [datetime]::ParseExact(($now.ToString('yyyy-MM-dd') + ' ' + $useStart), 'yyyy-MM-dd HH:mm', $null)
    $todayEnd   = [datetime]::ParseExact(($now.ToString('yyyy-MM-dd') + ' ' + $useEnd), 'yyyy-MM-dd HH:mm', $null)
    if ($todayEnd -le $todayStart) { $todayEnd = $todayEnd.AddDays(1) }

    if ($dayOk -and $now -ge $todayStart -and $now -lt $todayEnd) {
        $out.State = 'Inside'; $out.Start = $todayStart; $out.End = $todayEnd; $out.Remain = $todayEnd - $now
        return $out
    }

    # 跨零点时段：判断昨天开始的窗口是否仍在生效
    $yStart = $todayStart.AddDays(-1)
    $yEnd   = $todayEnd.AddDays(-1)
    $yDayOk = ($ScheduleDays.Count -eq 0) -or ($ScheduleDays -contains [int]$yStart.DayOfWeek)
    if ($yDayOk -and $now -ge $yStart -and $now -lt $yEnd) {
        $out.State = 'Inside'; $out.Start = $yStart; $out.End = $yEnd; $out.Remain = $yEnd - $now
        return $out
    }

    if ($dayOk -and $now -lt $todayStart) {
        $out.State = 'Before'; $out.Start = $todayStart; $out.End = $todayEnd
    } else {
        $out.State = 'Closed'; $out.Start = $todayStart; $out.End = $todayEnd
    }
    return $out
}

function Stop-GameNow {
    param([string]$Reason = '')
    $ps = @(Get-GameProcesses)
    foreach ($p in $ps) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Write-Log ('结束游戏，原因：' + $Reason)
    $script:dueTime    = $null
    $script:roundStart = $null
    $script:extendUsed = 0
    return $ps.Count
}

function Show-NoticeDialog {
    param(
        [string]$Title = '提示',
        [string]$Message = '',
        [string]$Hint = '',
        [int]$Seconds = 10,
        [string]$CountdownText = '提示将在 {0} 秒后自动关闭',
        [string]$CapturePath = ''
    )

    $script:noticeRemain = $Seconds

    $form = New-UiForm -Title '游戏时间提醒' -W 360 -H 190 -NoControlBox
    Add-UiTopBar -Form $form -W 360

    $lblTitle = New-UiText -Text $Title -X 20 -Y 24 -W 320 -H 40 -Size 16 -Bold -Color $script:C_Primary -Align 'MiddleCenter'

    $lblMsg = New-UiText -Text $Message -X 20 -Y 68 -W 320 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleCenter'

    $lblHint = New-UiText -Text $Hint -X 20 -Y 96 -W 320 -H 20 -Size 9 -Color $script:C_Sub -Align 'MiddleCenter'

    $lblCount = New-UiText -Text ($CountdownText -f $script:noticeRemain) -X 20 -Y 118 -W 320 -H 20 -Size 9 -Color $script:C_Sub -Align 'MiddleCenter'

    $btnOk = New-UiBtn -Text '知道了' -X 125 -Y 142 -W 110 -H 34

    $form.Controls.AddRange(@($lblTitle, $lblMsg, $lblHint, $lblCount, $btnOk))

    $script:nForm     = $form
    $script:nCount    = $lblCount
    $script:nTemplate = $CountdownText

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 1000
    $script:nTimer = $timer

    $btnOk.Add_Click({
        $script:nTimer.Stop()
        $script:nForm.Close()
    })

    $timer.Add_Tick({
        $script:noticeRemain--
        if ($script:noticeRemain -le 0) {
            $script:nTimer.Stop()
            $script:nForm.Close()
        } else {
            $script:nCount.Text = ($script:nTemplate -f $script:noticeRemain)
        }
    })

    $form.Add_Shown({
        $script:nTimer.Start()
        try { [System.Media.SystemSounds]::Exclamation.Play() } catch { }
    })
    if ($CapturePath) {
        $script:nShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1200
        $script:nCap = $cp
        $cp.Add_Tick({
            $script:nCap.Stop()
            try {
                $b = $script:nForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:nShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
        })
        $form.Add_Shown({ $script:nCap.Start() })
    }
    $form.Add_FormClosed({
        $script:nTimer.Stop()
        $script:nTimer.Dispose()
    })

    [void]$form.ShowDialog()
    $form.Dispose()
}

function Show-SettingsDialog {
    param(
        [int]$AutoClose = 0,
        [string]$CapturePath = ''
    )
    $script:settingsSaved = $false

    $form = New-UiForm -Title '游戏时长设置' -W 400 -H 372
    Add-UiTopBar -Form $form -W 400

    $sec1 = New-UiText -Text '可玩时段' -X 20 -Y 20 -W 200 -H 26 -Size 11 -Bold -Color $script:C_Text

    $lbl1 = New-UiText -Text '开始时间' -X 20 -Y 55 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $dtpStart = New-UiTimePicker -X 90 -Y 52 -W 104 -Value $ScheduleStart

    $lbl2 = New-UiText -Text '结束时间' -X 204 -Y 55 -W 68 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $dtpEnd = New-UiTimePicker -X 276 -Y 52 -W 104 -Value $ScheduleEnd

    $lbl3 = New-UiText -Text '结束前预警' -X 20 -Y 91 -W 78 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $numWarn = New-UiNumber -X 104 -Y 88 -W 74 -Min 1 -Max 60 -Value ([int]$ScheduleWarnMin)

    $lbl4 = New-UiText -Text '分钟' -X 184 -Y 91 -W 48 -H 26 -Size 9 -Color $script:C_Sub

    $lbl5 = New-UiText -Text '生效星期' -X 20 -Y 127 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $dayNames = @('日', '一', '二', '三', '四', '五', '六')
    $script:sDayChecks = @()
    for ($i = 0; $i -lt 7; $i++) {
        $checked = $false
        if ($ScheduleDays.Count -eq 0) { $checked = $true }
        else { $checked = ($ScheduleDays -contains $i) }
        $cb = New-UiCheck -Text $dayNames[$i] -X (90 + $i * 42) -Y 126 -W 40 -H 28 -Checked $checked
        $form.Controls.Add($cb)
        $script:sDayChecks += $cb
    }

    $line1 = New-UiLine -X 20 -Y 164 -W 360

    $lblTip = New-UiText -Text '保存后立即生效，并写入 config.json' -X 20 -Y 292 -W 360 -H 20 -Size 9 -Color $script:C_Sub

    $btnSave = New-UiBtn -Text '保存' -X 168 -Y 318 -W 100 -H 36 -Primary

    $btnCancel = New-UiBtn -Text '取消' -X 280 -Y 318 -W 100 -H 36

    $cbContinuous = New-UiCheck -Text '时段内同时启用连续时长限制' -X 20 -Y 170 -W 300 -H 30 -Checked ([bool]$ScheduleWithContinuous)

    # ---- 连续时长规则参数 ----
    $sepLine = New-UiLine -X 20 -Y 206 -W 360

    $lblPlay = New-UiText -Text '连续时长' -X 20 -Y 215 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $numPlay = New-UiNumber -X 90 -Y 212 -W 74 -Min 5 -Max 480 -Value ([int]$PlayMinutes)

    $lblPlayUnit = New-UiText -Text '分钟' -X 170 -Y 215 -W 48 -H 26 -Size 9 -Color $script:C_Sub

    $lblExtend = New-UiText -Text '每次续期' -X 20 -Y 251 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $numExtend = New-UiNumber -X 90 -Y 248 -W 74 -Min 1 -Max 60 -Value ([int]$ExtendMinutes)

    $lblExtendUnit = New-UiText -Text '分钟' -X 170 -Y 251 -W 48 -H 26 -Size 9 -Color $script:C_Sub

    $lblMax = New-UiText -Text '最多' -X 224 -Y 251 -W 48 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $numMax = New-UiNumber -X 278 -Y 248 -W 62 -Min 0 -Max 10 -Value ([int]$MaxExtend)

    $lblMaxUnit = New-UiText -Text '次' -X 344 -Y 251 -W 24 -H 26 -Size 9 -Color $script:C_Sub

    $form.Controls.AddRange(@($sec1, $lbl1, $dtpStart, $lbl2, $dtpEnd, $lbl3, $numWarn, $lbl4, $lbl5, $line1, $lblTip, $cbContinuous, $sepLine, $lblPlay, $numPlay, $lblPlayUnit, $lblExtend, $numExtend, $lblExtendUnit, $lblMax, $numMax, $lblMaxUnit, $btnSave, $btnCancel))

    $script:sForm     = $form
    $script:sDtpStart = $dtpStart
    $script:sDtpEnd   = $dtpEnd
    $script:sNumWarn  = $numWarn
    $script:sCbCont   = $cbContinuous
    $script:sNumPlay   = $numPlay
    $script:sNumExtend = $numExtend
    $script:sNumMax    = $numMax

    $btnCancel.Add_Click({ $script:sForm.Close() })

    $btnSave.Add_Click({
        $st = $script:sDtpStart.Value.ToString('HH:mm')
        $en = $script:sDtpEnd.Value.ToString('HH:mm')
        if ($st -eq $en) {
            [void][System.Windows.Forms.MessageBox]::Show('开始时间和结束时间不能相同。', '可玩时段设置')
            return
        }
        $days = @()
        for ($i = 0; $i -lt 7; $i++) {
            if ($script:sDayChecks[$i].Checked) { $days += $i }
        }
        if ($days.Count -eq 0) {
            [void][System.Windows.Forms.MessageBox]::Show('至少选择一个生效星期。', '可玩时段设置')
            return
        }
        $script:ScheduleStart   = $st
        $script:ScheduleEnd     = $en
        $script:ScheduleWarnMin = [int]$script:sNumWarn.Value
        $script:ScheduleDays    = $days
        $script:ScheduleWithContinuous = [bool]$script:sCbCont.Checked
        $script:PlayMinutes   = [int]$script:sNumPlay.Value
        $script:ExtendMinutes = [int]$script:sNumExtend.Value
        $script:MaxExtend     = [int]$script:sNumMax.Value
        $script:warnKey         = $null
        $script:settingsSaved   = $true
        Save-Config
        if (Get-Command Update-ModeMenu -ErrorAction SilentlyContinue) { Update-ModeMenu }
        Write-Log ('更新设置：时段 {0}-{1}，预警 {2} 分，连续 {3} 分，续期 {4} 分×{5} 次，星期 {6}' -f $st, $en, $script:ScheduleWarnMin, $script:PlayMinutes, $script:ExtendMinutes, $script:MaxExtend, ($days -join ','))
        $script:sForm.Close()
    })

    if ($AutoClose -gt 0) {
        $ac = New-Object System.Windows.Forms.Timer
        $ac.Interval = $AutoClose * 1000
        $script:acTimer = $ac
        $ac.Add_Tick({ $script:acTimer.Stop(); $script:sForm.Close() })
    }
    if ($CapturePath) {
        $script:uShotPath = $CapturePath
        $uc = New-Object System.Windows.Forms.Timer
        $uc.Interval = 1200
        $script:uCap = $uc
        $uc.Add_Tick({
            $script:uCap.Stop()
            try {
                $b = $script:sForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:uShotPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose()
                $bmp.Dispose()
            } catch { }
        })
    }
    $form.Add_Shown({
        if ($script:acTimer) { $script:acTimer.Start() }
        if ($script:uCap) { $script:uCap.Start() }
    })

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:settingsSaved
}

function Show-TempDialog {
    param(
        [int]$AutoClose = 0,
        [string]$CapturePath = ''
    )
    $script:tempSaved = $false
    $todayKey = (Get-Date).ToString('yyyy-MM-dd')

    $baseStart = $ScheduleStart
    $baseEnd   = $ScheduleEnd
    if ($TempDate -eq $todayKey -and $TempStart -and $TempEnd) {
        $baseStart = $TempStart
        $baseEnd   = $TempEnd
    }

    $form = New-UiForm -Title '临时调整今天' -W 400 -H 182
    Add-UiTopBar -Form $form -W 400

    $lbl1 = New-UiText -Text '开始时间' -X 20 -Y 59 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $dtpStart = New-UiTimePicker -X 90 -Y 56 -W 104 -Value $baseStart

    $lbl2 = New-UiText -Text '结束时间' -X 204 -Y 59 -W 68 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $dtpEnd = New-UiTimePicker -X 276 -Y 56 -W 104 -Value $baseEnd

    $lblTip = New-UiText -Text '仅对今天生效，明天自动恢复常规时段' -X 20 -Y 22 -W 360 -H 22 -Size 9 -Color $script:C_Sub

    $btnSave = New-UiBtn -Text '今天就这样' -X 152 -Y 126 -W 116 -H 36 -Primary

    $btnCancel = New-UiBtn -Text '取消' -X 280 -Y 126 -W 100 -H 36

    $form.Controls.AddRange(@($lbl1, $dtpStart, $lbl2, $dtpEnd, $lblTip, $btnSave, $btnCancel))

    $script:tForm     = $form
    $script:tDtpStart = $dtpStart
    $script:tDtpEnd   = $dtpEnd

    $btnCancel.Add_Click({ $script:tForm.Close() })

    $btnSave.Add_Click({
        $st = $script:tDtpStart.Value.ToString('HH:mm')
        $en = $script:tDtpEnd.Value.ToString('HH:mm')
        if ($st -eq $en) {
            [void][System.Windows.Forms.MessageBox]::Show('开始时间和结束时间不能相同。', '临时调整')
            return
        }
        $script:TempDate  = (Get-Date).ToString('yyyy-MM-dd')
        $script:TempStart = $st
        $script:TempEnd   = $en
        $script:warnKey   = $null
        $script:tempSaved = $true
        Save-Config
        if (Get-Command Update-ModeMenu -ErrorAction SilentlyContinue) { Update-ModeMenu }
        Write-Log ('临时调整今天：{0} - {1}（仅今日生效）' -f $st, $en)
        $script:tForm.Close()
    })

    if ($AutoClose -gt 0) {
        $ac = New-Object System.Windows.Forms.Timer
        $ac.Interval = $AutoClose * 1000
        $script:tAc = $ac
        $ac.Add_Tick({ $script:tAc.Stop(); $script:tForm.Close() })
        $form.Add_Shown({ $script:tAc.Start() })
    }
    if ($CapturePath) {
        $script:tShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1500
        $script:tCap = $cp
        $cp.Add_Tick({
            $script:tCap.Stop()
            try {
                $b = $script:tForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:tShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
        })
        $form.Add_Shown({ $script:tCap.Start() })
    }

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:tempSaved
}

function Get-SteamGames {
    $result = @()
    $seenDirs = @{}
    $junkNames = @('crashpad_handler', 'unitycrashhandler', 'epicwebhelper', 'unrealcrashhelper', 'crashreporter', 'vcredist', 'dxsetup', 'unins')
    $steamPath = $null
    try { $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath } catch { }
    if (-not $steamPath) { return @() }

    $libs = @(($steamPath -replace '/', '\'))
    $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        try {
            $content = Get-Content $vdf -Raw -Encoding UTF8
            foreach ($m in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
                $p = ($m.Groups[1].Value -replace '\\\\', '\') -replace '/', '\'
                $pLow = $p.ToLower()
                $dup = $false
                foreach ($l in $libs) { if ($l.ToLower() -eq $pLow) { $dup = $true; break } }
                if (-not $dup) { $libs += $p }
            }
        } catch { }
    }

    foreach ($lib in $libs) {
        $common = Join-Path $lib 'steamapps\common'
        if (-not (Test-Path $common)) { continue }
        foreach ($dir in @(Get-ChildItem $common -Directory -ErrorAction SilentlyContinue)) {
            if ($dir.Name -eq 'Steamworks Shared' -or $dir.Name -eq 'Steam Controller Configs') { continue }
            $dirLow = $dir.FullName.ToLower()
            if ($seenDirs.ContainsKey($dirLow)) { continue }
            $seenDirs[$dirLow] = $true

            # 按常见游戏目录结构精准匹配（快速），失败再深递归兜底
            $exe = $null
            $patterns = @(
                (Join-Path $dir.FullName '*.exe'),
                (Join-Path $dir.FullName '*\*.exe'),
                (Join-Path $dir.FullName '*\Binaries\Win64\*.exe'),
                (Join-Path $dir.FullName '*\Binaries\Win64\*\*.exe'),
                (Join-Path $dir.FullName '*\Binaries\*\*.exe')
            )
            foreach ($pat in $patterns) {
                if ($exe) { break }
                $cands = @(Get-ChildItem -Path $pat -File -ErrorAction SilentlyContinue | Where-Object {
                    $n = $_.BaseName.ToLower()
                    $skip = $false
                    foreach ($j in $junkNames) { if ($n -like "*$j*") { $skip = $true; break } }
                    -not $skip
                } | Sort-Object Length -Descending)
                if ($cands.Count -gt 0) { $exe = $cands[0] }
            }
            if (-not $exe) {
                try {
                    $exe = Get-ChildItem $dir.FullName -Filter *.exe -Recurse -Depth 6 -File -ErrorAction SilentlyContinue |
                           Where-Object {
                               $n = $_.BaseName.ToLower()
                               $skip = $false
                               foreach ($j in $junkNames) { if ($n -like "*$j*") { $skip = $true; break } }
                               -not $skip
                           } | Sort-Object Length -Descending | Select-Object -First 1
                } catch { }
            }
            $result += [pscustomobject]@{
                Name = $dir.Name
                Exe  = if ($exe) { $exe.BaseName } else { '' }
                Path = $dir.FullName
            }
        }
    }
    return @($result)
}

function Update-GameListBox {
    if (-not $script:gListBox) { return }
    $script:gListBox.Items.Clear()
    foreach ($g in $script:gTemp) {
        [void]$script:gListBox.Items.Add(('{0}    [{1}]' -f $g.Name, (@($g.Processes) -join ', ')))
    }
}

function Show-PickSteamGames {
    param([string]$CapturePath = '')
    $all = @(Get-SteamGames)
    if ($all.Count -eq 0) {
        [void][System.Windows.Forms.MessageBox]::Show('没有扫描到 Steam 游戏。请确认 Steam 已安装且库目录可访问。', '扫描结果')
        return @()
    }

    $script:pickAll    = $all
    $script:pickResult = @()

    $form = New-UiForm -Title '选择要限制的游戏（可多选）' -W 460 -H 400
    Add-UiTopBar -Form $form -W 460

    $lbl = New-UiText -Text ('扫描到 {0} 个游戏，按住 Ctrl 可多选' -f $all.Count) -X 20 -Y 20 -W 420 -H 22 -Size 9 -Color $script:C_Sub

    $list = New-Object System.Windows.Forms.ListBox
    $list.Bounds = New-Object System.Drawing.Rectangle((U 20), (U 48), (U 420), (U 286))
    $list.SelectionMode = 'MultiExtended'
    $list.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)
    foreach ($g in $all) {
        [void]$list.Items.Add(('{0}    ({1})' -f $g.Name, $(if ($g.Exe) { $g.Exe + '.exe' } else { '未找到主程序' })))
    }

    $btnOk = New-UiBtn -Text '添加选中' -X 208 -Y 346 -W 124 -H 36 -Primary

    $btnCancel = New-UiBtn -Text '取消' -X 344 -Y 346 -W 96 -H 36

    $form.Controls.AddRange(@($lbl, $list, $btnOk, $btnCancel))
    $script:pForm = $form
    $script:pList = $list

    $btnCancel.Add_Click({ $script:pForm.Close() })
    $btnOk.Add_Click({
        $sel = @()
        foreach ($idx in $script:pList.SelectedIndices) { $sel += $script:pickAll[$idx] }
        $script:pickResult = $sel
        $script:pForm.Close()
    })

    if ($CapturePath) {
        $script:pShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1200
        $script:pCap = $cp
        $cp.Add_Tick({
            $script:pCap.Stop()
            try {
                $b = $script:pForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:pShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
            try { $script:pForm.Close() } catch { }
        })
        $form.Add_Shown({ $script:pCap.Start() })
    }

    [void]$form.ShowDialog()
    $form.Dispose()
    return @($script:pickResult)
}

function Show-AddGameDialog {
    param(
        [int]$AutoClose = 0,
        [string]$CapturePath = ''
    )
    $script:addResult = $null

    $form = New-UiForm -Title '手动添加游戏' -W 420 -H 250
    Add-UiTopBar -Form $form -W 420

    $lbl1 = New-UiText -Text '游戏名称' -X 20 -Y 31 -W 68 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $txtName = New-Object System.Windows.Forms.TextBox
    $txtName.Bounds = New-Object System.Drawing.Rectangle((U 96), (U 30), (U 300), (U 30))
    $txtName.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)

    $lbl2 = New-UiText -Text '进程名' -X 20 -Y 73 -W 68 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'

    $txtProc = New-Object System.Windows.Forms.TextBox
    $txtProc.Bounds = New-Object System.Drawing.Rectangle((U 96), (U 72), (U 300), (U 30))
    $txtProc.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)

    $lblTip = New-UiText -Text "进程名不含 .exe，多个用逗号分隔。`n游戏运行时可在「任务管理器 → 详细信息」里查看进程名。" -X 96 -Y 112 -W 300 -H 58 -Size 9 -Color $script:C_Sub

    $btnOk = New-UiBtn -Text '添加' -X 192 -Y 190 -W 100 -H 36 -Primary

    $btnCancel = New-UiBtn -Text '取消' -X 304 -Y 190 -W 96 -H 36

    $form.Controls.AddRange(@($lbl1, $txtName, $lbl2, $txtProc, $lblTip, $btnOk, $btnCancel))
    $script:aForm = $form
    $script:aName = $txtName
    $script:aProc = $txtProc

    $btnCancel.Add_Click({ $script:aForm.Close() })
    $btnOk.Add_Click({
        $nm = $script:aName.Text.Trim()
        $raw = $script:aProc.Text.Trim()
        if (-not $nm -or -not $raw) {
            [void][System.Windows.Forms.MessageBox]::Show('游戏名称和进程名都要填写。', '手动添加游戏')
            return
        }
        $procs = @($raw -split '[,，\s]+' | Where-Object { $_ } | ForEach-Object { ($_ -replace '\.exe$', '') })
        $script:addResult = [pscustomobject]@{
            Name       = $nm
            Processes  = $procs
            PathFilter = @()
        }
        $script:aForm.Close()
    })

    if ($AutoClose -gt 0) {
        $ac = New-Object System.Windows.Forms.Timer
        $ac.Interval = $AutoClose * 1000
        $script:aAc = $ac
        $ac.Add_Tick({ $script:aAc.Stop(); $script:aForm.Close() })
        $form.Add_Shown({ $script:aAc.Start() })
    }
    if ($CapturePath) {
        $script:aShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1500
        $script:aCap = $cp
        $cp.Add_Tick({
            $script:aCap.Stop()
            try {
                $b = $script:aForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:aShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
        })
        $form.Add_Shown({ $script:aCap.Start() })
    }

    [void]$form.ShowDialog()
    $form.Dispose()
    return $script:addResult
}

function Show-GamesDialog {
    param(
        [int]$AutoClose = 0,
        [string]$CapturePath = ''
    )
    $script:gTemp = @()
    foreach ($g in $Games) {
        $script:gTemp += [pscustomobject]@{
            Name       = $g.Name
            Processes  = @($g.Processes)
            PathFilter = @($g.PathFilter)
        }
    }

    $form = New-UiForm -Title '管理游戏列表' -W 460 -H 390
    Add-UiTopBar -Form $form -W 460

    $lblTip = New-UiText -Text '下列游戏共用同一套时长规则：任一游戏运行即开始计时' -X 20 -Y 20 -W 420 -H 22 -Size 9 -Color $script:C_Sub

    $list = New-Object System.Windows.Forms.ListBox
    $list.Bounds = New-Object System.Drawing.Rectangle((U 20), (U 48), (U 420), (U 216))
    $list.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 10)

    $btnSteam = New-UiBtn -Text '从 Steam 库添加' -X 20 -Y 274 -W 136 -H 34

    $btnManual = New-UiBtn -Text '手动添加' -X 166 -Y 274 -W 100 -H 34

    $btnDel = New-UiBtn -Text '删除选中' -X 276 -Y 274 -W 100 -H 34

    $line1 = New-UiLine -X 20 -Y 324 -W 420

    $btnSave = New-UiBtn -Text '保存并关闭' -X 208 -Y 336 -W 124 -H 36 -Primary

    $btnCancel = New-UiBtn -Text '取消' -X 344 -Y 336 -W 96 -H 36

    $form.Controls.AddRange(@($lblTip, $list, $btnSteam, $btnManual, $btnDel, $line1, $btnSave, $btnCancel))
    $script:gForm = $form
    $script:gListBox = $list
    Update-GameListBox

    $btnCancel.Add_Click({ $script:gForm.Close() })

    $btnDel.Add_Click({
        $idxs = @($script:gListBox.SelectedIndices)
        if ($idxs.Count -eq 0) { return }
        $newList = @()
        for ($i = 0; $i -lt $script:gTemp.Count; $i++) {
            if ($idxs -notcontains $i) { $newList += $script:gTemp[$i] }
        }
        $script:gTemp = $newList
        Update-GameListBox
    })

    $btnSteam.Add_Click({
        $picked = @(Show-PickSteamGames)
        if ($picked.Count -eq 0) { return }
        foreach ($pg in $picked) {
            if (-not $pg.Exe) { continue }
            $exists = $false
            foreach ($ex in $script:gTemp) { if ($ex.Name -eq $pg.Name) { $exists = $true; break } }
            if (-not $exists) {
                $script:gTemp += [pscustomobject]@{
                    Name       = $pg.Name
                    Processes  = @($pg.Exe)
                    PathFilter = @()
                }
            }
        }
        Update-GameListBox
    })

    $btnManual.Add_Click({
        $newGame = Show-AddGameDialog
        if ($null -ne $newGame) {
            $script:gTemp += $newGame
            Update-GameListBox
        }
    })

    $btnSave.Add_Click({
        if ($script:gTemp.Count -eq 0) {
            $ans = [System.Windows.Forms.MessageBox]::Show('列表为空将不会管控任何游戏，确定保存吗？', '管理游戏列表', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
            if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        }
        $script:Games = @($script:gTemp)
        Save-Config
        if (Get-Command Update-ModeMenu -ErrorAction SilentlyContinue) { Update-ModeMenu }
        Write-Log ('游戏列表已更新：' + (($script:Games | ForEach-Object { $_.Name }) -join '、'))
        $script:gForm.Close()
    })

    if ($AutoClose -gt 0) {
        $ac = New-Object System.Windows.Forms.Timer
        $ac.Interval = $AutoClose * 1000
        $script:gAc = $ac
        $ac.Add_Tick({ $script:gAc.Stop(); $script:gForm.Close() })
        $form.Add_Shown({ $script:gAc.Start() })
    }
    if ($CapturePath) {
        $script:gShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1500
        $script:gCap = $cp
        $cp.Add_Tick({
            $script:gCap.Stop()
            try {
                $b = $script:gForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:gShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose()
                $bmp.Dispose()
            } catch { }
        })
        $form.Add_Shown({ $script:gCap.Start() })
    }

    [void]$form.ShowDialog()
    $form.Dispose()
}

# ==================== 主控制面板（左键点击托盘图标打开）====================
function Get-PanelStatusText {
    $modeText = ''
    if ($UseSchedule) {
        $modeText = '模式：固定时段 {0} - {1}' -f $ScheduleStart, $ScheduleEnd
    } else {
        $modeText = '模式：连续时长 {0} 分钟/轮，可续 {1} 次' -f $PlayMinutes, $MaxExtend
    }
    $todayKey = (Get-Date).ToString('yyyy-MM-dd')
    if ($TempDate -eq $todayKey -and $TempStart -and $TempEnd) {
        $modeText += ('  |  今日临时 {0}-{1}' -f $TempStart, $TempEnd)
    }
    $paused = $false
    try { $paused = [bool](Get-PausedState) } catch { $paused = $false }
    if ($paused) { $modeText += '  |  已暂停' }

    $gameText = ''
    $procs = @()
    try { $procs = @(Get-GameProcesses) } catch { $procs = @() }
    if ($procs.Count -gt 0) {
        $g = $null
        try { $g = Get-ActiveGame } catch { }
        $nm = '游戏'
        if ($g) { $nm = $g.Name }
        if ($script:dueTime) {
            $left = [int][Math]::Ceiling(($script:dueTime - (Get-Date)).TotalSeconds)
            if ($left -lt 0) { $left = 0 }
            $gameText = '{0} 运行中 · 本轮剩余 {1} 分 {2} 秒' -f $nm, [int][Math]::Floor($left / 60), ($left % 60)
        } else {
            $gameText = '{0} 运行中' -f $nm
        }
        $gameText = '● ' + $gameText
    } else {
        $gameText = '○ 当前没有游戏在运行'
    }
    return @($modeText, $gameText)
}

function Update-PanelState {
    if (-not $script:panelForm -or $script:panelForm.IsDisposed) { return }
    try {
        $st = Get-PanelStatusText
        $script:pLblMode.Text = $st[0]
        $script:pLblGame.Text = $st[1]
        $script:pRbSched.Checked = [bool]$script:UseSchedule
        $script:pRbCont.Checked  = -not [bool]$script:UseSchedule
        $paused = $false
        try { $paused = [bool](Get-PausedState) } catch { }
        if ($paused) { $script:pBtnPause.Text = '恢复管控' } else { $script:pBtnPause.Text = '暂停 1 小时' }
    } catch { }
}

function Show-MainPanel {
    param(
        [int]$AutoClose = 0,
        [string]$CapturePath = ''
    )

    # 已打开则置前，不重复创建
    if ($script:panelForm -and -not $script:panelForm.IsDisposed) {
        try {
            $script:panelForm.Show()
            $script:panelForm.Activate()
            $script:panelForm.BringToFront()
            Update-PanelState
            return
        } catch { }
    }

    $form = New-UiForm -Title '游戏时长守卫' -W 420 -H 576
    $form.TopMost = $false
    Add-UiTopBar -Form $form -W 420

    $lblTitle = New-UiText -Text '游戏时长守卫' -X 20 -Y 14 -W 300 -H 26 -Size 15 -Bold -Color $script:C_Text
    $lblMode  = New-UiText -Text '' -X 20 -Y 42 -W 380 -H 22 -Size 9 -Color $script:C_Sub
    $lblGame  = New-UiText -Text '' -X 20 -Y 64 -W 380 -H 22 -Size 9 -Color $script:C_Text
    $line1 = New-UiLine -X 20 -Y 92 -W 380

    $rbSched = New-UiRadio -Text '固定时段' -X 20 -Y 100 -W 110 -H 28 -Checked ([bool]$UseSchedule)
    $rbCont  = New-UiRadio -Text '连续时长' -X 140 -Y 100 -W 120 -H 28 -Checked (-not [bool]$UseSchedule)
    $line2 = New-UiLine -X 20 -Y 136 -W 380

    $secA = New-UiText -Text '可玩时段' -X 20 -Y 142 -W 200 -H 22 -Size 10.5 -Bold -Color $script:C_Text

    $lbl1 = New-UiText -Text '开始时间' -X 20 -Y 171 -W 64 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $dtpStart = New-UiTimePicker -X 90 -Y 168 -W 104 -Value $ScheduleStart
    $lbl2 = New-UiText -Text '结束时间' -X 206 -Y 171 -W 68 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $dtpEnd = New-UiTimePicker -X 276 -Y 168 -W 104 -Value $ScheduleEnd

    $lbl3 = New-UiText -Text '结束前预警' -X 20 -Y 207 -W 78 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $numWarn = New-UiNumber -X 104 -Y 204 -W 74 -Min 1 -Max 60 -Value ([int]$ScheduleWarnMin)
    $lblUnitWarn = New-UiText -Text '分钟' -X 182 -Y 207 -W 48 -H 26 -Size 9 -Color $script:C_Sub

    $lbl5 = New-UiText -Text '生效星期' -X 20 -Y 243 -W 78 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $dayNames = @('日', '一', '二', '三', '四', '五', '六')
    $script:pDayChecks = @()
    for ($i = 0; $i -lt 7; $i++) {
        $ck = $false
        if ($ScheduleDays.Count -eq 0) { $ck = $true } else { $ck = ($ScheduleDays -contains $i) }
        $cb = New-UiCheck -Text $dayNames[$i] -X (104 + $i * 42) -Y 242 -W 40 -H 28 -Checked $ck
        $form.Controls.Add($cb)
        $script:pDayChecks += $cb
    }
    $line3 = New-UiLine -X 20 -Y 280 -W 380

    $secB = New-UiText -Text '连续时长' -X 20 -Y 286 -W 200 -H 22 -Size 10.5 -Bold -Color $script:C_Text

    $lblPlay = New-UiText -Text '连续时长' -X 20 -Y 315 -W 78 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $numPlay = New-UiNumber -X 104 -Y 312 -W 74 -Min 5 -Max 480 -Value ([int]$PlayMinutes)
    $lblUnitPlay = New-UiText -Text '分钟' -X 182 -Y 315 -W 48 -H 26 -Size 9 -Color $script:C_Sub

    $lblExt = New-UiText -Text '每次续期' -X 20 -Y 351 -W 78 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $numExtend = New-UiNumber -X 104 -Y 348 -W 74 -Min 1 -Max 60 -Value ([int]$ExtendMinutes)
    $lblUnitExt = New-UiText -Text '分钟' -X 182 -Y 351 -W 48 -H 26 -Size 9 -Color $script:C_Sub
    $lblMax = New-UiText -Text '最多' -X 232 -Y 351 -W 48 -H 26 -Size 10 -Color $script:C_Text -Align 'MiddleRight'
    $numMax = New-UiNumber -X 286 -Y 348 -W 62 -Min 0 -Max 10 -Value ([int]$MaxExtend)
    $lblUnitMax = New-UiText -Text '次' -X 352 -Y 351 -W 24 -H 26 -Size 9 -Color $script:C_Sub

    $cbCont = New-UiCheck -Text '时段内同时启用连续时长限制' -X 20 -Y 384 -W 320 -H 30 -Checked ([bool]$ScheduleWithContinuous)
    $line4 = New-UiLine -X 20 -Y 420 -W 380

    $btnGames = New-UiBtn -Text '管理游戏列表' -X 20 -Y 428 -W 120 -H 34
    $btnTemp  = New-UiBtn -Text '临时调整今天' -X 152 -Y 428 -W 120 -H 34
    $btnPause = New-UiBtn -Text '暂停 1 小时' -X 284 -Y 428 -W 116 -H 34
    $btnKill  = New-UiBtn -Text '立即结束游戏' -X 20 -Y 470 -W 120 -H 34
    $btnExit  = New-UiBtn -Text '退出程序' -X 152 -Y 470 -W 100 -H 34
    $line5 = New-UiLine -X 20 -Y 514 -W 380

    $lblTip = New-UiText -Text '改完点保存生效' -X 20 -Y 527 -W 120 -H 26 -Size 9 -Color $script:C_Sub
    $btnSave  = New-UiBtn -Text '保存设置' -X 148 -Y 522 -W 124 -H 36 -Primary
    $btnClose = New-UiBtn -Text '关闭' -X 284 -Y 522 -W 96 -H 36

    $form.Controls.AddRange(@($lblTitle, $lblMode, $lblGame, $line1, $rbSched, $rbCont, $line2,
        $secA, $lbl1, $dtpStart, $lbl2, $dtpEnd, $lbl3, $numWarn, $lblUnitWarn, $lbl5,
        $line3, $secB, $lblPlay, $numPlay, $lblUnitPlay, $lblExt, $numExtend, $lblUnitExt,
        $lblMax, $numMax, $lblUnitMax, $cbCont, $line4, $btnGames, $btnTemp, $btnPause,
        $btnKill, $btnExit, $line5, $lblTip, $btnSave, $btnClose))

    $script:panelForm  = $form
    $script:pLblMode   = $lblMode
    $script:pLblGame   = $lblGame
    $script:pRbSched   = $rbSched
    $script:pRbCont    = $rbCont
    $script:pDtpStart  = $dtpStart
    $script:pDtpEnd    = $dtpEnd
    $script:pNumWarn   = $numWarn
    $script:pNumPlay   = $numPlay
    $script:pNumExtend = $numExtend
    $script:pNumMax    = $numMax
    $script:pCbCont    = $cbCont
    $script:pBtnPause  = $btnPause
    $script:pLblTip    = $lblTip

    # ---- 模式切换：立即生效 ----
    $rbSched.Add_CheckedChanged({
        if (-not $script:pRbSched.Checked) { return }
        if (Get-Command Switch-Mode -ErrorAction SilentlyContinue) { Switch-Mode -Schedule $true }
        Update-PanelState
    })
    $rbCont.Add_CheckedChanged({
        if (-not $script:pRbCont.Checked) { return }
        if (Get-Command Switch-Mode -ErrorAction SilentlyContinue) { Switch-Mode -Schedule $false }
        Update-PanelState
    })

    # ---- 保存设置 ----
    $btnSave.Add_Click({
        $st = $script:pDtpStart.Value.ToString('HH:mm')
        $en = $script:pDtpEnd.Value.ToString('HH:mm')
        if ($st -eq $en) {
            [void][System.Windows.Forms.MessageBox]::Show('开始时间和结束时间不能相同。', '游戏时长设置')
            return
        }
        $days = @()
        for ($i = 0; $i -lt 7; $i++) {
            if ($script:pDayChecks[$i].Checked) { $days += $i }
        }
        if ($days.Count -eq 0) {
            [void][System.Windows.Forms.MessageBox]::Show('至少选择一个生效星期。', '游戏时长设置')
            return
        }
        $script:ScheduleStart   = $st
        $script:ScheduleEnd     = $en
        $script:ScheduleWarnMin = [int]$script:pNumWarn.Value
        $script:ScheduleDays    = $days
        $script:ScheduleWithContinuous = [bool]$script:pCbCont.Checked
        $script:PlayMinutes   = [int]$script:pNumPlay.Value
        $script:ExtendMinutes = [int]$script:pNumExtend.Value
        $script:MaxExtend     = [int]$script:pNumMax.Value
        $script:warnKey       = $null
        Save-Config
        if (Get-Command Update-ModeMenu -ErrorAction SilentlyContinue) { Update-ModeMenu }
        Write-Log ('面板保存设置：时段 {0}-{1}，预警 {2} 分，连续 {3} 分，续期 {4} 分×{5} 次，星期 {6}' -f $st, $en, $script:ScheduleWarnMin, $script:PlayMinutes, $script:ExtendMinutes, $script:MaxExtend, ($days -join ','))
        Update-PanelState
        $script:pLblTip.Text = '已保存 ✓'
        $script:pLblTip.ForeColor = [System.Drawing.Color]::FromArgb(46, 125, 50)
        try { $script:pTipTimer.Stop(); $script:pTipTimer.Start() } catch { }
    })

    # ---- 快捷操作 ----
    $btnGames.Add_Click({ [void](Show-GamesDialog); Update-PanelState })
    $btnTemp.Add_Click({ [void](Show-TempDialog); Update-PanelState })
    $btnPause.Add_Click({
        $paused = $false
        try { $paused = [bool](Get-PausedState) } catch { }
        if ($paused) {
            if (Test-Path $PauseFlag) { Remove-Item $PauseFlag -Force -ErrorAction SilentlyContinue }
            Write-Log '面板：恢复管控'
        } else {
            (Get-Date).AddHours(1).ToString('yyyy-MM-dd HH:mm:ss') | Set-Content -Path $PauseFlag -Encoding ASCII
            Write-Log '面板：暂停管控 1 小时'
        }
        Update-PanelState
    })
    $btnKill.Add_Click({
        $ps = @(Get-GameProcesses)
        if ($ps.Count -eq 0) {
            [void][System.Windows.Forms.MessageBox]::Show('当前没有检测到被管控的游戏在运行。', '立即结束游戏')
            return
        }
        foreach ($p in $ps) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
        Write-Log '面板：立即结束游戏'
        Update-PanelState
    })
    $btnExit.Add_Click({
        $ans = [System.Windows.Forms.MessageBox]::Show(
            "确定要退出游戏时长守卫吗？`n`n退出后本次开机内不再有任何管控。`n（下次开机仍会自动启动）",
            '游戏时长守卫',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { return }
        Write-Log '用户从面板退出 GameGuard'
        try { $script:pTimer.Stop() } catch { }
        try { $notify.Visible = $false; $notify.Dispose() } catch { }
        [System.Windows.Forms.Application]::Exit()
        [Environment]::Exit(0)
    })
    $btnClose.Add_Click({ $script:panelForm.Close() })

    # ---- 状态刷新 ----
    $tmr = New-Object System.Windows.Forms.Timer
    $tmr.Interval = 1000
    $script:pTimer = $tmr
    $tmr.Add_Tick({ Update-PanelState })

    $tipTimer = New-Object System.Windows.Forms.Timer
    $tipTimer.Interval = 2200
    $script:pTipTimer = $tipTimer
    $tipTimer.Add_Tick({
        $script:pTipTimer.Stop()
        try {
            $script:pLblTip.Text = '改完点保存生效'
            $script:pLblTip.ForeColor = $script:C_Sub
        } catch { }
    })

    $form.Add_Shown({ $script:pTimer.Start(); Update-PanelState })
    $form.Add_FormClosed({
        try { $script:pTimer.Stop(); $script:pTimer.Dispose() } catch { }
        try { $script:pTipTimer.Stop(); $script:pTipTimer.Dispose() } catch { }
        $script:panelForm = $null
    })

    if ($AutoClose -gt 0) {
        $ac = New-Object System.Windows.Forms.Timer
        $ac.Interval = $AutoClose * 1000
        $script:pAc = $ac
        $ac.Add_Tick({ $script:pAc.Stop(); try { $script:panelForm.Close() } catch { } })
        $form.Add_Shown({ $script:pAc.Start() })
    }
    if ($CapturePath) {
        $script:pShot = $CapturePath
        $cp = New-Object System.Windows.Forms.Timer
        $cp.Interval = 1500
        $script:pCap = $cp
        $cp.Add_Tick({
            $script:pCap.Stop()
            try {
                $b = $script:panelForm.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($script:pShot, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
        })
        $form.Add_Shown({ $script:pCap.Start() })
    }

    $form.Show()
}

# 自绘盾牌图标，不依赖 SystemIcons（避免系统资源缺失导致图标为空）
function New-GuardIcon {
    $bmp = New-Object System.Drawing.Bitmap(32, 32)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $pts = [System.Drawing.Point[]]@(
        (New-Object System.Drawing.Point(16, 2)),
        (New-Object System.Drawing.Point(29, 7)),
        (New-Object System.Drawing.Point(29, 17)),
        (New-Object System.Drawing.Point(16, 30)),
        (New-Object System.Drawing.Point(3, 17)),
        (New-Object System.Drawing.Point(3, 7))
    )
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(198, 40, 40))
    $g.FillPolygon($brush, $pts)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2)
    $g.DrawPolygon($pen, $pts)
    $font = New-Object System.Drawing.Font('Arial', 9, [System.Drawing.FontStyle]::Bold)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 7, 32, 18)
    $g.DrawString('60', $font, [System.Drawing.Brushes]::White, $rect, $sf)
    $g.Dispose()
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    return $icon
}

# ==================== 配置文件 ====================
$ConfigPath = Join-Path $ScriptDir 'config.json'

function Save-Config {
    $cfg = [ordered]@{
        games            = @($Games | ForEach-Object {
                                [ordered]@{
                                    name       = $_.Name
                                    processes  = @($_.Processes)
                                    pathFilter = @($_.PathFilter)
                                }
                            })
        useSchedule      = $UseSchedule
        scheduleStart    = $ScheduleStart
        scheduleEnd      = $ScheduleEnd
        scheduleWarnMin  = $ScheduleWarnMin
        scheduleDays     = $ScheduleDays
        outsideCountdown = $OutsideCountdown
        scheduleWithContinuous = $ScheduleWithContinuous
        playMinutes      = $PlayMinutes
        extendMinutes    = $ExtendMinutes
        maxExtend        = $MaxExtend
        tempDate         = $TempDate
        tempStart        = $TempStart
        tempEnd          = $TempEnd
    }
    try {
        ($cfg | ConvertTo-Json -Depth 4) | Set-Content -Path $ConfigPath -Encoding UTF8
        Write-Log '配置已保存到 config.json'
    } catch {
        Write-Log ('配置保存失败：' + $_.Exception.Message)
    }
}

function Import-Config {
    if (-not (Test-Path $ConfigPath)) {
        Write-Log '未发现 config.json，使用脚本内默认值并生成一份'
        Save-Config
        return
    }
    try {
        $raw = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $map = @{
            useSchedule      = 'UseSchedule'
            scheduleStart    = 'ScheduleStart'
            scheduleEnd      = 'ScheduleEnd'
            scheduleWarnMin  = 'ScheduleWarnMin'
            scheduleDays     = 'ScheduleDays'
            outsideCountdown = 'OutsideCountdown'
            scheduleWithContinuous = 'ScheduleWithContinuous'
            playMinutes      = 'PlayMinutes'
            extendMinutes    = 'ExtendMinutes'
            maxExtend        = 'MaxExtend'
            tempDate         = 'TempDate'
            tempStart        = 'TempStart'
            tempEnd          = 'TempEnd'
        }
        foreach ($k in $map.Keys) {
            $prop = $raw.PSObject.Properties[$k]
            if ($prop -and $null -ne $prop.Value) {
                Set-Variable -Name $map[$k] -Value $prop.Value -Scope Script
            }
        }
        # 游戏列表单独还原为对象数组
        if ($raw.PSObject.Properties['games'] -and $raw.games) {
            $loaded = @()
            foreach ($g in @($raw.games)) {
                $loaded += [pscustomobject]@{
                    Name       = [string]$g.name
                    Processes  = @($g.processes | ForEach-Object { [string]$_ })
                    PathFilter = @($g.pathFilter | ForEach-Object { [string]$_ })
                }
            }
            if ($loaded.Count -gt 0) { $script:Games = $loaded }
        }
        Write-Log ('已加载 config.json，当前模式：' + $(if ($UseSchedule) { '固定时段' } else { '连续时长' }))
    } catch {
        Write-Log ('config.json 读取失败，回退默认值：' + $_.Exception.Message)
    }

    # 类型规范化，避免 JSON 数字/布尔类型差异影响判断
    $script:UseSchedule      = [bool]$UseSchedule
    $script:ScheduleWithContinuous = [bool]$ScheduleWithContinuous
    $script:ScheduleDays     = @($ScheduleDays | ForEach-Object { [int]$_ })
    $script:ScheduleWarnMin  = [int]$ScheduleWarnMin
    $script:OutsideCountdown = [int]$OutsideCountdown
    $script:PlayMinutes      = [int]$PlayMinutes
    $script:ExtendMinutes    = [int]$ExtendMinutes
    $script:MaxExtend        = [int]$MaxExtend
}

Import-Config

# ==================== 自测模式 ====================
if ($SelfTest) {
    # 验证托盘图标与单实例锁在真实环境中可用
    try {
        $testIcon = New-GuardIcon
        Write-Output ("SelfTest icon: OK (handle={0})" -f $testIcon.Handle)
    } catch {
        Write-Output ("SelfTest icon: FAILED - " + $_.Exception.Message)
    }
    try {
        $m = New-Object System.Threading.Mutex($false, 'Local\GameGuard_SelfTest')
        [void]$m.WaitOne(0)
        Write-Output 'SelfTest mutex: OK'
    } catch {
        Write-Output ("SelfTest mutex: FAILED - " + $_.Exception.Message)
    }
    Write-Output 'SelfTest: 弹窗将在 8 秒后自动关闭'
    $shot = Join-Path $ScriptDir 'selftest.png'
    $r = Show-GuardDialog -PlayedMinutes 60 -RemainExtend 2 -TimeoutSeconds 8 -CapturePath $shot
    Write-Output ("SelfTest result: {0}" -f $r)
    if (Test-Path $shot) { Write-Output ("Screenshot: {0}" -f $shot) }
    exit 0
}

# ==================== 时段模式自测 ====================
if ($ScheduleTest) {
    Write-Output 'ScheduleTest: 显示时段预警弹窗（5 秒后自动关闭）'
    Show-NoticeDialog -Title '可游玩时间剩余 3 分钟' -Message ('{0} 到点将自动结束游戏' -f $ScheduleEnd) -Hint ('可玩时段：' + $ScheduleStart + ' - ' + $ScheduleEnd) -Seconds 5 -CountdownText '提示将在 {0} 秒后自动关闭'
    Write-Output 'ScheduleTest done'
    exit 0
}

# ==================== 界面自测 ====================
if ($UiTest) {
    Write-Output 'UiTest 1/5: 设置窗口（7 秒）'
    [void](Show-SettingsDialog -AutoClose 7 -CapturePath (Join-Path $ScriptDir 'ui_snap.png'))
    Write-Output 'UiTest 2/5: 临时调整窗口（5 秒）'
    Show-TempDialog -AutoClose 5 -CapturePath (Join-Path $ScriptDir 'temp_snap.png')
    Write-Output 'UiTest 3/5: 手动添加窗口（5 秒）'
    Show-AddGameDialog -AutoClose 5 -CapturePath (Join-Path $ScriptDir 'add_snap.png')
    Write-Output 'UiTest 4/5: 提醒弹窗（6 秒）'
    Show-NoticeDialog -Title '可游玩时间剩余 3 分钟' -Message '22:30 到点将自动结束游戏' -Hint '可玩时段：20:00 - 22:30' -Seconds 6 -CapturePath (Join-Path $ScriptDir 'notice_snap.png')
    Write-Output 'UiTest 5/5: Steam 选择窗口'
    Show-PickSteamGames -CapturePath (Join-Path $ScriptDir 'pick_snap.png')
    Write-Output 'UiTest done'
    exit 0
}

# ==================== 游戏列表窗口自测 ====================
if ($GamesTest) {
    Write-Output 'GamesTest: 显示游戏管理窗口（8 秒后自动关闭）'
    Show-GamesDialog -AutoClose 8 -CapturePath (Join-Path $ScriptDir 'games_snap.png')
    Write-Output 'GamesTest done'
    exit 0
}

# ==================== 控制面板自测 ====================
if ($PanelTest) {
    Write-Output 'PanelTest: 打开主控制面板（10 秒后自动关闭）'
    Show-MainPanel -AutoClose 10 -CapturePath (Join-Path $ScriptDir 'panel_snap.png')
    $deadline = (Get-Date).AddSeconds(14)
    while ((Get-Date) -lt $deadline) {
        [System.Windows.Forms.Application]::DoEvents()
        if (-not $script:panelForm -or $script:panelForm.IsDisposed) { break }
        Start-Sleep -Milliseconds 100
    }
    Write-Output 'PanelTest done'
    exit 0
}

# ==================== 主循环 ====================
Write-Log '===== GameGuard 启动 ====='

# 单实例锁：避免重复双击产生多个托盘图标
$script:mutex = New-Object System.Threading.Mutex($false, 'Local\GameGuard_SingleInstance')
$gotLock = $false
try {
    $gotLock = $script:mutex.WaitOne(0)
}
catch [System.Threading.AbandonedMutexException] {
    # 上个实例异常退出未释放锁；此时锁所有权其实已拿到，应视为获取成功
    $gotLock = $true
}
catch { $gotLock = $false }
if (-not $gotLock) {
    Write-Log '已有实例在运行，本次启动退出'
    exit 0
}

$script:dueTime    = $null
$script:roundStart = $null
$script:extendUsed = 0
$script:warnKey    = $null

$notify = New-Object System.Windows.Forms.NotifyIcon
try {
    $notify.Icon = New-GuardIcon
    $notify.Text = '游戏时长守卫（运行中）'
    $notify.Visible = $true
    Write-Log '托盘图标已创建'
    $tipText = if ($UseSchedule) { ('可玩时段 {0} - {1}，结束前 {2} 分钟预警。左键点击图标打开控制面板。' -f $ScheduleStart, $ScheduleEnd, $ScheduleWarnMin) } else { ('连续游玩 {0} 分钟会提醒你。左键点击图标打开控制面板。' -f $PlayMinutes) }
    $notify.ShowBalloonTip(4000, '游戏时长守卫已启动', $tipText, [System.Windows.Forms.ToolTipIcon]::Info)
} catch {
    Write-Log ('托盘图标创建失败：' + $_.Exception.Message)
}

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$itemPanel = $menu.Items.Add('打开控制面板...')
$itemStatus = $menu.Items.Add('状态：等待游戏启动')
$itemStatus.Enabled = $false
[void]$menu.Items.Add('-')

# 模式切换子菜单
$menuMode = New-Object System.Windows.Forms.ToolStripMenuItem('切换模式')
$itemModeSched = New-Object System.Windows.Forms.ToolStripMenuItem('固定时段')
$itemModeCont  = New-Object System.Windows.Forms.ToolStripMenuItem('连续时长（60 分钟 + 续期）')
$itemModeSched.CheckOnClick = $false
$itemModeCont.CheckOnClick = $false
[void]$menuMode.DropDownItems.Add($itemModeSched)
[void]$menuMode.DropDownItems.Add($itemModeCont)
[void]$menu.Items.Add($menuMode)

$itemScheduleSettings = $menu.Items.Add('时长与时段设置...')
$itemGames = $menu.Items.Add('管理游戏列表...')
$itemTempToday = $menu.Items.Add('临时调整今天...')
$itemTempClear = $menu.Items.Add('清除临时调整')

[void]$menu.Items.Add('-')
$itemPause = $menu.Items.Add('暂停管控（1 小时）')
$itemResume = $menu.Items.Add('恢复管控')
$itemKillNow = $menu.Items.Add('立即结束游戏')
[void]$menu.Items.Add('-')
$itemExit = $menu.Items.Add('退出程序')
$notify.ContextMenuStrip = $menu

# 左键点击托盘图标 → 打开主控制面板（右键仍保留快捷菜单）
$notify.Add_MouseClick({
    $evt = $args[1]
    if ($null -ne $evt -and $evt.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        try { Show-MainPanel } catch { Write-Log ('打开控制面板失败：' + $_.Exception.Message) }
    }
})
$notify.Add_MouseDoubleClick({
    $evt = $args[1]
    if ($null -ne $evt -and $evt.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
        try { Show-MainPanel } catch { }
    }
})
$itemPanel.Add_Click({ try { Show-MainPanel } catch { } })

function Update-ModeMenu {
    $itemModeSched.Text = ('固定时段（{0} - {1}）' -f $ScheduleStart, $ScheduleEnd)
    $itemModeSched.Checked = [bool]$UseSchedule
    $itemModeCont.Checked = -not [bool]$UseSchedule
    $menuMode.Text = ('当前模式：' + $(if ($UseSchedule) { '固定时段' } else { '连续时长' }))

    $todayKey = (Get-Date).ToString('yyyy-MM-dd')
    if ($TempDate -eq $todayKey -and $TempStart -and $TempEnd) {
        $itemTempToday.Text = ('临时调整今天（今日 {0} - {1}）' -f $TempStart, $TempEnd)
        $itemTempClear.Enabled = $true
    } else {
        $itemTempToday.Text = '临时调整今天...'
        $itemTempClear.Enabled = $false
    }

    try {
        $notify.Text = $(if ($UseSchedule) { '游戏时长守卫（固定时段）' } else { '游戏时长守卫（连续时长）' })
    } catch { }
}

function Switch-Mode {
    param([bool]$Schedule)
    if ([bool]$UseSchedule -eq $Schedule) { Update-ModeMenu; return }
    $script:UseSchedule = $Schedule
    $script:dueTime    = $null
    $script:roundStart = $null
    $script:extendUsed = 0
    $script:warnKey    = $null
    Save-Config
    Update-ModeMenu
    Write-Log ('切换模式：' + $(if ($Schedule) { '固定时段（' + $ScheduleStart + ' - ' + $ScheduleEnd + '）' } else { '连续时长（' + $PlayMinutes + ' 分钟 + 续期 ' + $MaxExtend + ' 次）' }))
}

$itemModeSched.Add_Click({ Switch-Mode -Schedule $true })
$itemModeCont.Add_Click({ Switch-Mode -Schedule $false })

$itemScheduleSettings.Add_Click({ [void](Show-SettingsDialog) })

$itemGames.Add_Click({ [void](Show-GamesDialog) })

$itemTempToday.Add_Click({ [void](Show-TempDialog) })

$itemTempClear.Add_Click({
    $script:TempDate  = ''
    $script:TempStart = ''
    $script:TempEnd   = ''
    $script:warnKey   = $null
    Save-Config
    Update-ModeMenu
    Write-Log '已清除今日临时调整，恢复常规时段'
})

Update-ModeMenu

function Get-PausedState {
    if (Test-Path $PauseFlag) {
        try {
            $raw = (Get-Content $PauseFlag -Raw -ErrorAction SilentlyContinue).Trim()
            if ($raw -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$') {
                $until = [datetime]::ParseExact($raw, 'yyyy-MM-dd HH:mm:ss', $null)
                if ((Get-Date) -lt $until) { return $true }
                Remove-Item $PauseFlag -Force -ErrorAction SilentlyContinue
                return $false
            }
            return $true
        } catch { return $false }
    }
    return $false
}

$itemPause.Add_Click({
    (Get-Date).AddHours(1).ToString('yyyy-MM-dd HH:mm:ss') | Set-Content -Path $PauseFlag -Encoding ASCII
    Write-Log '用户暂停管控 1 小时'
})
$itemResume.Add_Click({
    if (Test-Path $PauseFlag) { Remove-Item $PauseFlag -Force -ErrorAction SilentlyContinue }
    Write-Log '用户恢复管控'
})
$itemKillNow.Add_Click({
    $ps = @(Get-GameProcesses)
    foreach ($p in $ps) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
    Write-Log '用户通过托盘立即结束游戏'
})
$itemExit.Add_Click({
    $ans = [System.Windows.Forms.MessageBox]::Show(
        "确定要退出游戏时长守卫吗？`n`n退出后本次开机内不再有任何管控。`n（下次开机仍会自动启动）",
        '游戏时长守卫',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    Write-Log '用户退出 GameGuard'
    $notify.Visible = $false
    $notify.Dispose()
    [System.Windows.Forms.Application]::Exit()
    [Environment]::Exit(0)
})

while ($true) {
    try {
        [System.Windows.Forms.Application]::DoEvents()

        $paused = Get-PausedState
        if ($paused) {
            $itemStatus.Text = '状态：已暂停'
        }
        else {
            $procs = @(Get-GameProcesses)
            $sched = Get-ScheduleState

            if ($UseSchedule) {
                # ============ 固定时段模式 ============
                if ($sched.State -eq 'Inside') {
                    $remainSec = [int][Math]::Ceiling($sched.Remain.TotalSeconds)
                    $nearEnd   = ($remainSec -le ($ScheduleWarnMin * 60))

                    # 连续时长规则：紧邻时段结束前不再触发（时段约束优先）
                    if ($procs.Count -gt 0 -and $ScheduleWithContinuous -and (-not $nearEnd)) {
                        if ($null -eq $script:dueTime) {
                            $script:roundStart = Get-Date
                            $script:dueTime = (Get-Date).AddMinutes($PlayMinutes)
                            Write-Log ('时段内叠加连续时长，本轮截止 {0}' -f $script:dueTime.ToString('HH:mm:ss'))
                        }
                    }

                    # 状态文本
                    if ($procs.Count -gt 0) {
                        if ($null -ne $script:dueTime) {
                            $leftSec = [int][Math]::Ceiling(($script:dueTime - (Get-Date)).TotalSeconds)
                            if ($leftSec -lt 0) { $leftSec = 0 }
                            $itemStatus.Text = ('状态：游玩中，连续剩 {0} 分 / 时段剩 {1} 分' -f [int][Math]::Floor($leftSec / 60), [int][Math]::Floor($remainSec / 60))
                        } else {
                            $itemStatus.Text = ('状态：游玩中，可玩时段剩余 {0} 分 {1} 秒' -f [int][Math]::Floor($remainSec / 60), ($remainSec % 60))
                        }
                    } else {
                        $itemStatus.Text = ('状态：可玩时段内，剩余 {0} 分' -f [int][Math]::Ceiling($remainSec / 60))
                    }

                    if ($remainSec -le 0) {
                        if ($procs.Count -gt 0) { [void](Stop-GameNow '可玩时段结束') }
                        $itemStatus.Text = '状态：可玩时段已结束'
                    }
                    elseif (-not $nearEnd) {
                        # 连续时长到点：弹续期窗口
                        if ($procs.Count -gt 0 -and $null -ne $script:dueTime -and (Get-Date) -ge $script:dueTime) {
                            Invoke-Curfew
                        }
                    }
                    else {
                        # 临近时段结束：只做预警，到点强杀，不再弹续期
                        $warnKey = $sched.End.ToString('yyyyMMddHHmm')
                        if ($script:warnKey -ne $warnKey) {
                            $script:warnKey = $warnKey
                            $warnMin = [int][Math]::Ceiling($remainSec / 60)
                            Write-Log ('可玩时间仅剩 {0} 分 {1} 秒，弹出预警' -f [int][Math]::Floor($remainSec / 60), ($remainSec % 60))
                            Show-NoticeDialog -Title ('可游玩时间剩余 {0} 分钟' -f $warnMin) -Message ('{0} 到点将自动结束游戏' -f $sched.End.ToString('HH:mm')) -Hint ('可玩时段：' + $sched.Start.ToString('HH:mm') + ' - ' + $sched.End.ToString('HH:mm')) -Seconds 10 -CountdownText '提示将在 {0} 秒后自动关闭'
                        }
                    }

                    # 游戏退出后重置连续计时
                    if ($procs.Count -eq 0 -and $null -ne $script:dueTime) {
                        Write-Log '游戏已退出，连续计时重置'
                        $script:dueTime    = $null
                        $script:roundStart = $null
                        $script:extendUsed = 0
                    }
                }
                else {
                    # 时段外：立即提示并关闭游戏
                    if ($procs.Count -gt 0) {
                        $realStart = $sched.Start.ToString('HH:mm')
                        $realEnd   = $sched.End.ToString('HH:mm')
                        if ($sched.State -eq 'Before') {
                            $msg = ('可玩时段还没开始（{0} - {1}）' -f $realStart, $realEnd)
                            $itemStatus.Text = '状态：不在可玩时段，正在关闭游戏'
                        } else {
                            $msg = ('今天的可玩时段已结束（{0} - {1}）' -f $realStart, $realEnd)
                            $itemStatus.Text = '状态：可玩时段已结束，正在关闭游戏'
                        }
                        Write-Log ('时段外检测到游戏运行，{0} 秒后关闭' -f $OutsideCountdown)
                        Show-NoticeDialog -Title '现在不在可玩时段' -Message $msg -Hint ('每天可玩：{0} - {1}' -f $realStart, $realEnd) -Seconds $OutsideCountdown -CountdownText '{0} 秒后关闭游戏'
                        [void](Stop-GameNow '不在可玩时段')
                    }
                    elseif ($sched.State -eq 'Before') {
                        $itemStatus.Text = ('状态：未到可玩时段（{0} 开始）' -f $sched.Start.ToString('HH:mm'))
                    } else {
                        $itemStatus.Text = '状态：今天可玩时段已结束'
                    }
                }
            }
            else {
                # ============ 连续时长模式（原逻辑）============
                if ($procs.Count -gt 0) {
                    if ($null -eq $script:dueTime) {
                        $script:roundStart = Get-Date
                        $script:dueTime = (Get-Date).AddMinutes($PlayMinutes)
                        Write-Log ('检测到游戏运行，本轮截止 {0}' -f $script:dueTime.ToString('HH:mm:ss'))
                    }
                    $leftSec = [int][Math]::Ceiling(($script:dueTime - (Get-Date)).TotalSeconds)
                    $itemStatus.Text = ('状态：游玩中，剩余 {0} 分 {1} 秒' -f [int][Math]::Floor($leftSec / 60), ($leftSec % 60))

                    if ((Get-Date) -ge $script:dueTime) {
                        Invoke-Curfew
                    }
                }
                else {
                    if ($null -ne $script:dueTime) {
                        Write-Log '游戏已退出，计时重置'
                        $script:dueTime    = $null
                        $script:roundStart = $null
                        $script:extendUsed = 0
                    }
                    $itemStatus.Text = '状态：等待游戏启动'
                }
            }
        }
    }
    catch {
        Write-Log ('循环异常：{0}' -f $_.Exception.Message)
    }

    # 分段休眠，保证托盘菜单仍可响应
    $waited = 0
    while ($waited -lt $PollSeconds) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
        $waited += 0.2
    }
}
