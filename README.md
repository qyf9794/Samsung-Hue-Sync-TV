# Samsung Hue Sync TV

在兼容的三星 Tizen 电视上读取画面颜色，通过 Philips Hue Bridge 的 Entertainment API V2 将颜色同步到娱乐区域。电视负责运行同步；日常使用不需要电脑持续运行。

> 这是实验性个人项目，不是 Philips Hue 或 Samsung 的官方应用。它使用电视设备上的私有测色接口，只能在具备该接口且允许调用的型号和固件上工作；安装成功不代表所有信号源都能取色。

## 需要什么

### 硬件

| 设备 | 条件 |
| --- | --- |
| 电视 | 三星 Tizen 智能电视，需能开启开发者模式，并提供可调用的 `/usr/lib/libvideoenhance.so` 测色接口。当前实机研究型号为 **QA75QN85BAJXXZ，Tizen 6.5**；其他型号、系统版本或固件未经验证。 |
| Hue Bridge | 支持 Entertainment API V2 和 Hue Entertainment 流的 Hue Bridge，需与电视在可互通的局域网内。 |
| 灯具 | 支持加入 Hue 娱乐区域的彩色灯具。渐变灯带可用的独立颜色通道数量由 Bridge 和娱乐区域报告，取决于设备与配置。 |
| 开发电脑 | 用于构建、签名和安装应用。电视 Developer Mode 需配置可连接的开发电脑 IP；VPN 或多网卡可能影响连接。 |

原型曾在上述电视上验证基础取色、Bridge 配对及同步链路，七点取色和按实际时间平滑的最新组合仍需实机验证。高清、4K、HDR、受保护内容、电视菜单及不同 HDMI/内置应用画面的可取色性和色彩表现未全面验证。

### 软件和工具

- 电视端：Tizen 6.5 环境和可运行 .NET Core 3.1 Tizen 服务的运行时；核心包声明 TV API 6.5。Web 界面声明最低 Tizen 5.5，但**不代表整体程序支持 5.5**。
- 构建端：.NET SDK（开发时使用 8.x，目标框架为 `netcoreapp3.1`）、Python 3、Node.js（运行 Web 测试时需要），以及 NuGet 依赖恢复网络。`.NET Core 3.1` 已停止支持；是否能在其他 Tizen 版本运行需要自行验证。
- 电视调试：启用 [Samsung TV Developer Mode](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-device.html)，配置正确的开发机 IP 并连接 SDB。
- 设备签名：覆盖电视 DUID 的 Samsung 开发者证书。两个包必须使用**同一个 author 证书**，以便 Tizen 可信消息端口工作；更新已有安装时保留原 author 证书。[Samsung 证书说明](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html)
- 安装工具：Tizen Studio/SDK，或支持重新签名和安装 WGT、TPK 的 [Apps2Samsung CLI](https://github.com/Apps2Samsung/Apps2Samsung)。以下示例使用后者。

## 工作方式

1. `HueSyncTV1`：电视 Web 界面和 Bridge 查询服务，负责配对、选择娱乐区域及向核心发送控制指令。
2. `HueEngine1`：Tizen .NET 后台服务。调用电视的 `cs_ve_get_rgb_measure_condition`、`cs_ve_set_rgb_measure_position`、`cs_ve_get_rgb_measure_pixel`，通过 HueStream 2.0 / DTLS 将颜色发送给 Bridge。
3. 配置中的 Hue X 用于画面左右，Z 用于画面高度。对于横跨屏幕且恰有三个通道的灯带，采样七个小区域，按 `0–2`、`2–4`、`4–6` 求均值；随后进行默认 30% 的相邻通道混色和时间平滑。采样点、颜色范围、刷新频率以及最终视觉效果都依赖电视和区域配置。
4. 时间平滑参考 [VocalTV](https://github.com/qyf9794/VocalTV_ios)：低、标准、高分别采用 0.55、0.25、0.12 秒的时间常数；按实际采样间隔计算平滑比例。它不增加灯带的物理控制段，也不是 VocalTV 的画面取色实现。

电视测色是设备专属接口，示例设备一次可测两个位置；七点取色因此需要多轮测量，响应速度可能降低。此项目并未接入 Philips Hue EDK；使用的是自行实现的 Hue Entertainment V2 协议和 DTLS。

## 构建

在仓库根目录执行：

```sh
cd tv-app
dotnet build HueTvSync.csproj -c Release -o build
mkdir -p deployment
cp build/HueTvSync.dll build/HueTvSync.deps.json build/HueTvSync.runtimeconfig.json build/BouncyCastle.Cryptography.dll deployment/
python3 build-tpk.py
python3 build-wgt.py
```

得到 `HueEngine-unsigned.tpk` 和 `HueTvSync-unsigned.wgt`，需要安装工具用自己的设备证书签名。首次构建 WGT 会生成随机控制令牌；保留同一 WGT 可在后续重建时复用令牌。不要公开 WGT、令牌、签名证书或 Hue Bridge 凭据。

## 安装和配对

1. 在 Hue app 中创建娱乐区域并放置灯具。开启电视 Developer Mode，设置为开发电脑实际 IP 并重启电视。
2. 使用**同一 author 证书**签名并安装核心 TPK 和界面 WGT。示例地址仅供替换，以下命令需要已安装并配置好 `apps2samsung-cli`：

   ```sh
   TV_IP=192.0.2.10
   HOST_IP=192.0.2.20
   apps2samsung-cli cert --tv "$TV_IP"
   apps2samsung-cli install HueEngine-unsigned.tpk --tv "$TV_IP" --local-ip "$HOST_IP"
   apps2samsung-cli install HueTvSync-unsigned.wgt --tv "$TV_IP" --local-ip "$HOST_IP"
   ```

3. 电视上打开“画面灯光同步”，输入 Bridge 的局域网 IP，按下 Bridge 实体按钮，再选择“配对 Bridge”。选择娱乐区域后开始同步。
4. 退出界面并切换到需要同步的画面；重新打开应用可以停止同步或调整敏感度。信号源是否可测色需在目标设备上验证。

更新已有安装时不要随意卸载应用或更换 author 证书，否则可能丢失配对配置或无法原位更新。首次配对采用首次信任方式保存 Bridge 证书指纹，后续连接检查该指纹；首次连接本身不验证证书链。Bridge 密钥和控制令牌属于敏感信息，不应提交到仓库或附在问题反馈中。

## 本地验证

```sh
cd tv-app
DOTNET_ROLL_FORWARD=Major dotnet build/HueTvSync.dll selftest --offline
node tizen-web/service/pairing.test.js
node tizen-web/service/service-pairing.test.js
node tizen-web/ui-pairing.test.js
node tizen-web/remote-navigation.test.js
```

离线自检覆盖坐标、七点均值、平滑和 HueStream 编码，不覆盖电视测色、DTLS 网络连接、签名安装或视觉效果；这些仍需实机验证。

## 第三方组件

核心依赖 [Bouncy Castle C#](https://www.bouncycastle.org/download/bouncy-castle-c/)，使用其 DTLS 实现。许可证见 [BOUNCY-CASTLE-LICENSE.md](BOUNCY-CASTLE-LICENSE.md)。
