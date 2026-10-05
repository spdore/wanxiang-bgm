# 万象 BGM 播放器

Windows 10/11 桌面软件，全局快捷键播放 MP3，将所选麦克风和软件音乐混合到 VB-CABLE。默认空曲库，可添加/移除歌曲及修改快捷键；耳机与虚拟通道音乐音量独立调节。

## 下载安装

[下载安装包](https://github.com/spdore/wanxiang-bgm/raw/refs/heads/main/WanxiangBgmSetup.exe)

安装包内含 VB-CABLE 普通版完整官方驱动。检测到已有驱动时跳过；缺少驱动时可选择打开官方驱动安装程序。需要管理员授权，按官方提示安装并重启。程序本体安装到当前用户目录。

首次启动没有歌曲，点击“添加歌曲”导入自己的 MP3，再点击按键栏绑定快捷键。播放时按任意曲目键停止，最多同时一首。耳机输出跟随 Windows 默认设备；聊天软件麦克风选择 **CABLE Output**，播放器写入 **CABLE Input**。

## 系统要求

Windows 10/11 和 .NET Framework 4.8。安装包尚未签名。虚拟音频驱动为第三方软件；卸载播放器不会移除共享驱动，也不会删除个人歌曲和设置。

## 构建

在项目根目录运行 PowerShell：

    .\build.ps1
    .\build-share.ps1

源码是 C# / WinForms，使用 .NET Framework 编译器。输出 BgmHotkey.exe 或分享安装包目录内的安装 EXE。

## 数据与隐私

分享版默认歌曲列表和快捷键为空，数据保存在当前使用者的 `%LOCALAPPDATA%\BgmHotkeyShare`，导入歌曲保存在该目录的 bgm 子目录。仓库不包含开发者歌曲、设备设置、录音、日志、截图或本机绝对路径。提交使用 GitHub noreply 邮箱。

## 第三方组件

VB-CABLE 由 VB-Audio 提供，采用 donationware 模式，欢迎捐赠或购买许可。

- 官方来源：https://vb-audio.com/Cable/
- 分发规则：https://vb-audio.com/Services/licensing.htm

VBCABLE_Driver_Pack45.zip 为未修改的完整官方驱动包，包含原始 readme 和许可。专业领域分发须遵循官方额外许可条件；VB-CABLE A+B/C+D 不包含在本项目中。第三方组件保留各自版权和许可。