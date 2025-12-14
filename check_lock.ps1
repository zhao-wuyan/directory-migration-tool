# 文件占用诊断脚本
# 使用方法：以管理员身份运行 PowerShell，然后执行此脚本

param(
    [string]$Path = "C:\testMove\01"
)

Write-Host "=== 文件占用诊断工具 ===" -ForegroundColor Cyan
Write-Host "检查目录: $Path`n" -ForegroundColor Yellow

# 1. 检查目录是否存在
if (-not (Test-Path $Path)) {
    Write-Host "❌ 目录不存在！" -ForegroundColor Red
    exit
}

Write-Host "✅ 目录存在" -ForegroundColor Green

# 2. 检查权限
try {
    $acl = Get-Acl $Path
    Write-Host "✅ 有读取权限" -ForegroundColor Green

    # 尝试创建测试文件
    $testFile = Join-Path $Path "_test_write_$(Get-Random).tmp"
    "test" | Out-File $testFile -ErrorAction Stop
    Remove-Item $testFile -ErrorAction Stop
    Write-Host "✅ 有写入权限" -ForegroundColor Green
}
catch {
    Write-Host "❌ 权限不足: $($_.Exception.Message)" -ForegroundColor Red
}

# 3. 检查目录属性
$dirInfo = Get-Item $Path
Write-Host "`n目录属性:" -ForegroundColor Cyan
Write-Host "  只读: $($dirInfo.IsReadOnly)"
Write-Host "  隐藏: $(($dirInfo.Attributes -band [System.IO.FileAttributes]::Hidden) -ne 0)"
Write-Host "  系统: $(($dirInfo.Attributes -band [System.IO.FileAttributes]::System) -ne 0)"

# 4. 使用 Handle 工具检查占用（如果已安装）
Write-Host "`n检查占用进程..." -ForegroundColor Cyan
$handlePath = "C:\Windows\System32\handle.exe"
$handlePath64 = "C:\Program Files\Sysinternals\handle.exe"

$handle = $null
if (Test-Path $handlePath) { $handle = $handlePath }
elseif (Test-Path $handlePath64) { $handle = $handlePath64 }

if ($handle) {
    Write-Host "使用 Handle 工具检查..." -ForegroundColor Yellow
    & $handle $Path -accepteula
}
else {
    Write-Host "⚠️  未安装 Handle 工具" -ForegroundColor Yellow
    Write-Host "   可以从以下地址下载：https://learn.microsoft.com/en-us/sysinternals/downloads/handle" -ForegroundColor Gray
    Write-Host "`n使用备用方法：OpenFiles 命令" -ForegroundColor Yellow

    # 使用 openfiles 命令（需要管理员权限）
    $openFiles = openfiles /query /fo csv 2>$null | ConvertFrom-Csv
    $relatedFiles = $openFiles | Where-Object { $_."Open File (Path\executable)" -like "*$Path*" }

    if ($relatedFiles) {
        Write-Host "`n找到占用的文件:" -ForegroundColor Red
        $relatedFiles | Format-Table -Property "Hostname", "ID", "Accessed By", "Open File (Path\executable)"
    }
    else {
        Write-Host "✅ 未发现明显的文件占用" -ForegroundColor Green
    }
}

# 5. 尝试重命名测试（模拟 SimpleFileLockDetector 的检测方式）
Write-Host "`n执行重命名测试（模拟程序检测方式）..." -ForegroundColor Cyan
$tempName = "$Path`_test_$(Get-Random)"
try {
    Rename-Item -Path $Path -NewName (Split-Path $tempName -Leaf) -ErrorAction Stop
    Write-Host "✅ 重命名成功，恢复原名..." -ForegroundColor Green
    Rename-Item -Path $tempName -NewName (Split-Path $Path -Leaf) -ErrorAction Stop
    Write-Host "✅ 目录可以正常操作，没有被锁定" -ForegroundColor Green
}
catch {
    Write-Host "❌ 重命名失败（这就是程序报错的原因）" -ForegroundColor Red
    Write-Host "   错误: $($_.Exception.Message)" -ForegroundColor Red

    # 提供解决建议
    Write-Host "`n💡 解决建议:" -ForegroundColor Yellow
    Write-Host "   1. 关闭文件资源管理器中打开的该目录"
    Write-Host "   2. 关闭可能访问该目录的程序（IDE、终端、同步软件等）"
    Write-Host "   3. 使用资源监视器查找占用进程（Win+R 输入 resmon）"
    Write-Host "   4. 以管理员身份运行迁移程序"
    Write-Host "   5. 重启电脑后重试"
}

Write-Host "`n=== 诊断完成 ===" -ForegroundColor Cyan
