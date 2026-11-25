$ErrorActionPreference = 'Stop'
$file = Join-Path (Split-Path -Parent $PSScriptRoot) 'Plain Craft Launcher 2/Pages/PageOther/PageOtherAbout.xaml'
$doc = [Xml.XmlDocument]::new()
$doc.PreserveWhitespace = $true
$doc.Load($file)
$ns = [Xml.XmlNamespaceManager]::new($doc.NameTable)
$ns.AddNamespace('p','clr-namespace:PCL')
$ns.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
$doc.SelectSingleNode('//p:MyCard[@Title="关于"]',$ns).SetAttribute('Title','PCL 原作者与 CPT 来源')
$doc.SelectSingleNode('//*[@x:Name="ItemAboutPcl"]',$ns).SetAttribute('Title','CPT 方舟领航')
$doc.SelectSingleNode('//p:MyButton[@Text="赞助 PCL !"]',$ns).SetAttribute('Text','支持 PCL 原作者')
$support = $doc.SelectSingleNode('//p:MyCard[@Title="支持 CraftPilot"]',$ns)
$support.SetAttribute('Title','CPT 维护者 · HUASQ')
$support.InnerXml = @'
<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:PCL" Margin="25,40,25,18">
  <TextBlock Text="CPT 方舟领航由 HUASQ 基于 Plain Craft Launcher 独立维护。服务器部署、管理、账号中心和官网由本项目增补，与 PCL 官方独立。" TextWrapping="Wrap" Margin="0,0,0,12" />
  <TextBlock Text="CPT 增补内容 © 2026 HUASQ。PCL 原始代码、原图标和第三方组件的版权仍归各自权利人。" TextWrapping="Wrap" Margin="0,0,0,15" />
  <WrapPanel>
    <local:MyButton Text="支持 CPT · 爱发电" Width="165" Height="35" Margin="0,0,12,8" ColorType="Highlight" local:CustomEventService.EventType="打开网页" local:CustomEventService.EventData="https://afdian.com/a/HUASQlaodaHH" />
    <local:MyButton Text="CPT 官网" Width="120" Height="35" Margin="0,0,12,8" local:CustomEventService.EventType="打开网页" local:CustomEventService.EventData="https://craftpilot.tolike.cn" />
  </WrapPanel>
</StackPanel>
'@
foreach ($entry in @(@('特别鸣谢','PCL 上游贡献者致谢'),@('赞助者','PCL 上游历史赞助者'))) {
    $card = $doc.SelectSingleNode('//p:MyCard[@Title="'+$entry[0]+'"]',$ns)
    $card.SetAttribute('Title',$entry[1]); $card.SetAttribute('CanSwap','True'); $card.SetAttribute('IsSwapped','True')
}
$history = $doc.SelectSingleNode('//w:TextBlock[contains(@Text,"以下是近期的高级赞助者")]',$ns)
$history.SetAttribute('Text','以下名单来自原版 PCL 源码，保留上游历史致谢，不代表 CPT 的赞助记录。')
$grid = $doc.SelectSingleNode('//*[@x:Name="BtnDonateOutput"]',$ns).ParentNode
[void]$grid.ParentNode.RemoveChild($grid)
$doc.SelectSingleNode('//w:TextBlock[contains(@Text,"当下载失败、PCL 出错")]',$ns).SetAttribute('Text','CPT 安装量统计默认关闭。启用后仅向 craftpilot.tolike.cn 发送随机安装标识、软件版本和通道，可在设置中关闭；日活记录保留 90 天，一年未活跃安装记录自动清理。')
$doc.SelectSingleNode('//w:TextBlock[contains(@Text,"PCL 没有收集个人信息")]',$ns).SetAttribute('Text','Minecraft 密码和刷新令牌由当前 Windows 用户 DPAPI 在本机加密；SSH 密码仅用于本次操作。网站统计不接收账号、SSH 信息、服务器配置或硬件指纹。完整说明见 CPT 官网。')
$doc.SelectSingleNode('//w:TextBlock[contains(@Text,"计算机软件著作权登记号")]',$ns).SetAttribute('Text',"PCL 原版：Copyright © 成都瓜皮龙科技有限公司；原版软件著作权登记号：2020SR0875133；原版权联系邮箱：admin@meloong.com（非 CPT 联系方式）。`nCPT 增补内容：© 2026 HUASQ；维护入口：https://craftpilot.tolike.cn。`n非 Minecraft 官方产品，未经 Mojang 或 Microsoft 批准，也不与其关联。`n上游许可要求衍生名称以 PCL 开头；CPT 独立命名的正式分发须取得相应许可或调整名称。新增署名不转移上游版权。")
$source = $doc.SelectSingleNode('//p:MyButton[@Text="开源代码"]',$ns)
$source.SetAttribute('Text','PCL 上游源码')
$source.SetAttribute('CustomEventService.EventData','clr-namespace:PCL','https://github.com/Meloong-Git/PCL')
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false); $settings.OmitXmlDeclaration = $true
$writer = [Xml.XmlWriter]::Create($file,$settings)
try { $doc.Save($writer) } finally { $writer.Dispose() }
