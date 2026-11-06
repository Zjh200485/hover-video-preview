# 悬停视频预览 2.3.1

Windows 桌面悬停预览工具与 Edge / Chromium 配套扩展。视频在浏览器后台播放，鼠标移入指定触发区时显示画面，移开隐藏。

## 使用

1. 启动根目录的 `悬停视频预览.exe`，选择浏览器后台模式，设置触发区并复制连接码。
2. 初次使用时在 Edge 加载 `浏览器扩展` 文件夹。已加载过同一路径时，升级后按 Alt+Shift+R 载入新代码即可，无需删除或重新导入扩展。
3. 在视频标签页打开扩展，粘贴连接码并开始预览。之后可切换标签页或最小化浏览器。
4. 桌面程序重启会生成新连接码，需要在扩展中替换。浏览器撤销采集授权后需要手动重新连接。

扩展 2.3.1 继续使用接收协议 3，兼容现有 2.3.0 桌面程序；这次只需重新载入扩展。

## 本次修复

- 采集源限制为最大 1280×720、15 帧/秒，JPEG 发送最多 10 帧/秒；避免仅在输出时缩小、源头仍采集高分辨率画面。
- 图像转换超时后停止采集，避免不可取消的转换任务继续堆积；停止时释放画布缓冲区。
- 五分钟内最多自动重建采集三次，持续异常时停止并提示手动修复；重启后台服务或更换采集会话不会重置次数。

此前 2.3.0 的修复仍保留：

- 停止通知与恢复意图按会话匹配，避免状态查询导致停止后重新采集。
- 检查内部视频时钟，自动恢复意外暂停，并识别持续停滞。
- 切换视频失败不再残留旧视频的恢复请求。
- 接收端校验采集会话及发送序号，拒绝停止后的延迟帧和乱序旧帧。

## 构建与验证

Windows 自带 .NET Framework 4.x 编译器，无需额外桌面运行依赖。执行 `build.ps1` 构建。

Node.js：`node --test 浏览器扩展/tests/core.test.cjs 浏览器扩展/tests/recovery.test.cjs`。

桌面程序支持 `--self-test 输出目录` 和 `--background-test 输出目录`。`浏览器扩展/tests/AuditDesktop.cs` 用于接收端停止及乱序回归测试；编译成控制台程序后传入桌面 EXE 路径。真实 Edge 测试使用独立配置和临时端口，详见 `浏览器扩展/tests/browser-recovery.cjs`、`audit-browser.cjs`。

GitHub 保留源码和必要验证脚本。本机精简运行目录只需 EXE、EXE.config、个人 settings.xml、浏览器扩展运行文件及使用说明。个人设置、连接码、浏览器配置和截图不上传。

## 参考

采集架构对照 [Chrome 官方 tabCapture 示例](https://github.com/GoogleChrome/chrome-extensions-samples/tree/main/functional-samples/sample.tabcapture-recorder)；生命周期处理对照 [Screenity](https://github.com/alyssaxuu/screenity)。未引入录像、编辑、上传云端等功能。
