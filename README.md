# 本机端口与进程面板

离线查看本机正在监听的端口，以及占用端口的进程。不结束进程，也不访问网络。

需要 Windows，以及 .NET 8 桌面运行时。程序以管理员身份启动。

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```

发布后的程序是 `dist\port-panel.exe`。窗口固定在工作区中间。左键点击托盘图标可以显示或收起，点到窗口外面也会收进托盘。托盘菜单可以开关开机自启动，或退出程序。
