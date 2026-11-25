param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
# Exact UI phrases only. Keep namespaces, data paths and upstream attributions.
$phrases = [ordered]@{
    'PCL 无法' = 'CPT 无法'
    'PCL 没有访问' = 'CPT 没有访问'
    'PCL 没能找到' = 'CPT 没能找到'
    'PCL 不支持其他' = 'CPT 不支持其他'
    'PCL 不支持安装' = 'CPT 不支持安装'
    'PCL 可能不支持' = 'CPT 可能不支持'
    'PCL 为此找到了' = 'CPT 为此找到了'
    'PCL 怀疑' = 'CPT 怀疑'
    'PCL 未能找到' = 'CPT 未能找到'
    'PCL 获取到了' = 'CPT 获取到了'
    'PCL 的默认描述' = 'CPT 的默认描述'
    'PCL 显示的版本列表' = 'CPT 显示的版本列表'
    'PCL 当前所在文件夹' = 'CPT 当前所在文件夹'
    'PCL 所在文件夹' = 'CPT 所在文件夹'
    'PCL 在该文件夹中的配置文件' = 'CPT 在该文件夹中的配置文件'
    'PCL 自动分析' = 'CPT 自动分析'
    'PCL 将选择' = 'CPT 将选择'
    'PCL 将自动搜索' = 'CPT 将自动搜索'
    'PCL 会在启动游戏时' = 'CPT 会在启动游戏时'
    'PCL 的替换标记' = 'CPT 的替换标记'
    'PCL 当前显示的皮肤' = 'CPT 当前显示的皮肤'
    'PCL 会选择列表中' = 'CPT 会选择列表中'
    'PCL 优先选择' = 'CPT 优先选择'
    'PCL 所选择的节点' = 'CPT 所选择的节点'
    '让 PCL 连接它' = '让 CPT 连接它'
    'PCL 的下载、皮肤' = 'CPT 的下载、皮肤'
    '重启 PCL' = '重启 CPT'
    '启动 PCL' = '启动 CPT'
    '运行 PCL' = '运行 CPT'
    '右键 PCL' = '右键 CPT'
    'PCL 都需要申请管理员权限' = 'CPT 都需要申请管理员权限'
    '将 PCL 和 Java' = '将 CPT 和 Java'
    '确保 PCL 在低性能电脑上' = '确保 CPT 在低性能电脑上'
    'PCL 将会在每次打开时' = 'CPT 将会在每次打开时'
    'PCL 将会播放' = 'CPT 将会播放'
    '显示 PCL 图标' = '显示 CPT 图标'
    '拖进 PCL 窗口' = '拖进 CPT 窗口'
    'PCL 内置了一些 Minecraft' = 'CPT 内置了一些 Minecraft'
    'PCL 当前的主题颜色' = 'CPT 当前的主题颜色'
    '拉伸 PCL 窗口' = '拉伸 CPT 窗口'
    '对方的 PCL 版本' = '对方的 CPT 版本'
    '你的 PCL 版本太老了' = '你的 CPT 版本太老了'
    '更新 PCL 之后再联机' = '更新 CPT 之后再联机'
    '在 PCL 启动器中输入邀请码' = '在 CPT 启动器中输入邀请码'
    '[MOTD]PCL 联机房间' = '[MOTD]CPT 联机房间'
    '使用 PCL 安装匹配的游戏' = '使用 CPT 安装匹配的游戏'
    'PCL 将使用默认的 Steve' = 'CPT 将使用默认的 Steve'
    '拒绝了 PCL 申请的权限' = '拒绝了 CPT 申请的权限'
    '将 PCL 更新至最新版' = '将 CPT 更新至最新版'
    '回到 PCL，就可以正常登录' = '回到 CPT，就可以正常登录'
    'PCL 也是正常的' = 'CPT 也是正常的'
    'PCL 自定义离线皮肤资源包' = 'CPT 自定义离线皮肤资源包'
    '请向 PCL 提交反馈' = '请向 CPT 维护者提交反馈'
    '请向 PCL 作者提交反馈' = '请向 CPT 维护者提交反馈'
    '老版本 PCL，更新 PCL' = '老版本 CPT，更新 CPT'
    '检查更新] 更新 PCL' = '检查更新] 更新 CPT'
    '查看 PCL 的日志' = '查看 CPT 的日志'
    'PCL 是否为最新版本' = 'CPT 是否为最新版本'
    '加载 PCL 主页自定义信息失败' = '加载 CPT 主页自定义信息失败'
    'PCL 位置：' = 'CPT 位置：'
    '已有一个 CraftPilot 实例正在运行' = '已有一个 CPT 实例正在运行'
    'PCL 启动器程序' = 'CPT 启动器程序'
    '打包正式版 PCL，以便没有启动器的玩家安装整合包' = '打包当前 CPT 程序，以便没有启动器的玩家安装整合包'
    'PCL 个性化内容' = 'CPT 个性化内容'
    'CraftPilot-client.mrpack' = 'CPT-client.mrpack'
}
$source = Join-Path $Root 'Plain Craft Launcher 2'
$encoding = New-Object System.Text.UTF8Encoding($false)
$changed = 0
Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $_.Extension -in '.vb','.xaml' -and $_.FullName -notmatch '\\(obj|bin)\\' -and
    $_.Name -notin 'PageOtherAbout.xaml','Resources.Designer.vb','Settings.Designer.vb'
} | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName)
    $updated = $text
    foreach ($phrase in $phrases.Keys) { $updated = $updated.Replace($phrase, $phrases[$phrase]) }
    if ($text -cne $updated) {
        [IO.File]::WriteAllText($_.FullName, $updated, $encoding)
        $changed++
    }
}
Write-Output "Updated CPT UI phrases in $changed source files."
