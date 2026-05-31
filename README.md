
# MA2BT Pro

> 用于 **VRChat Avatar** 的 **MA Responsive 图层转 BlendTree** 优化器

> 遇到问题可以在官方 QQ 群 **798072555** 中反馈

MA2BT Pro 会在 Avatar 构建时自动运行，把可转换的 MA Responsive 图层合并到一个 BlendTree 图层中，以减少 Animator Layer 数量，降低动画控制器开销。

MA2BT Pro 基于 MA2BT 修改和扩展。

## 与 MA2BT 的关系

MA2BT Pro 基于 Null-K / MA2BT 修改和扩展。

原版 MA2BT 是一个简洁、轻量的工具，主要用于将 Modular Avatar 生成的响应式图层转换为 BlendTree。
MA2BT Pro 保留了这个核心思路，并在此基础上增加了一些面向复杂 Avatar 项目的扩展功能和保护逻辑。

---

## MA2BT Pro 的主要扩展

在 MA2BT 的基础上，MA2BT Pro 主要增加了以下扩展：

* 支持更多多状态图层处理
* 增强 Bool / Int 参数转换与保护
* 支持保留图层中的 Bool / Int 条件重写
* 支持合并相同结构的混合树和动画
* 支持图层名、状态名、参数名前缀排除
* 提供更详细的 Console 日志，方便排查跳过原因
* 可选扫描所有 FX 图层，用于尝试处理部分手动制作的简单图层

这些扩展主要是为了提高复杂项目中的兼容性和可控性。
如果某些图层无法安全转换，MA2BT Pro 会保留原图层，而不是强制处理。


## 工作原理

Modular Avatar 会为 Object Toggle、Material Setter、Shape Changer 等响应式组件生成独立的 Animator 图层。
当 Avatar 中使用大量响应式组件时，FX 图层数量会增加，从而带来额外开销。

MA2BT Pro 会在 Modular Avatar 构建处理完成后运行，分析符合条件的图层，并将可转换的图层合并到一个 BlendTree 图层中。

转换前：

```text
MA Responsive: Object Toggle
MA Responsive: Material Setter
MA Responsive: Shape Changer
```

转换后：

```text
MA_To_BlendTree_Layer
└── Direct BlendTree
```

无法安全转换的图层会被保留，不会强制处理。

## 依赖要求

- Unity 2022.3 及以上
- [VRChat Avatars SDK](https://creators.vrchat.com/sdk/) 3.x
- [Modular Avatar](https://modular-avatar.nadena.dev/) 1.x
- [NDMF](https://ndmf.nadena.dev/) (随 MA 自动安装)

---

## 安装方法

### VCC / VPM 安装

在 VCC 中添加以下仓库地址：

```text
https://zhuozhi233.github.io/vpm-listing/index.json
```

然后在项目的 Manage Project 中安装 **MA2BT Pro**。

---

## 使用方法

1. 选择 Avatar 根对象
2. 添加组件：

```text
Add Component > MA2BT Pro > MA2BT Pro
```

3. 正常 Build / Upload Avatar
4. MA2BT Pro 会在构建流程中自动运行
5. 可以在 Console 中查看 `[MA2BT Pro]` 日志

> ※ 如果你安装了 AAO 或其他会合并动画层的插件，生成的 MA_To_BlendTree_Layer 层会被这些插件进一步合并。可以先移除其他优化插件来测试合并的数量和效果。

## 选项说明

| 选项 | 默认值 | 说明 |
|---|---|---|
| **紧凑模式** | 开启 | 只生成需要的阈值，减少多余节点。 |
| **多状态图层** | 开启 | 允许转换包含多个条件状态的图层。 |
| **合并相同混合树 / 动画** | 开启 | 当多个图层的多状态参数结构完全一致时，尝试合并为同一个嵌套混合树，并合并相同状态的动画。 |
| **扫描所有图层** | 关闭 | 不仅扫描 MA 生成的层，也会尝试扫描所有 FX 图层 |

---

## 排除项

| 选项      | 默认值                              | 说明                                                                          |
| ------- | -------------------------------- | --------------------------------------------------------------------------- |
| 图层名前缀排除 | `lilycalInventory`、`AutoDresser` | 图层名以前缀列表中任意内容开头时，MA2BT Pro 会保留该图层，不进行转换。                                    |
| 状态名前缀排除 | `Root`、`root`                    | 任意状态名以前缀列表中任意内容开头时，MA2BT Pro 会保留整个图层，不进行转换。                                 |
| 参数名前缀排除 | 空                                | 参数名以前缀列表中任意内容开头时，MA2BT Pro 会保留使用该参数的图层，不转换该参数，也不会进行 Bool / Int 到 Float 的迁移。 |

## 可转换条件

通常需要满足以下条件，图层才会被转换：

- 图层名以 `MA Responsive:` 开头，如果开启 `扫描所有图层`，则也会尝试扫描其他 FX 图层。
- 图层包含有效的 State Machine
- 存在默认状态
- 存在可以解析的 Entry Transition
- 条件参数可以转换为 BlendTree 使用的参数范围
- Transition 设置可以安全转换
- 状态或状态机中没有不安全的行为组件
- 图层、状态、参数没有被前缀排除

不满足条件的图层会被保留。

## 常见跳过原因

MA2BT Pro 只会转换可以安全合并到 BlendTree 的图层。
如果图层结构不符合转换条件，会保留原图层，不会强制处理。

| 原因                         | 说明                                                                       |
| -------------------------- | ------------------------------------------------------------------------ |
| 图层不符合扫描范围                  | 默认只扫描 `MA Responsive:` 图层。未开启“扫描所有图层”时，普通 FX 图层不会被处理。                    |
| 被前缀排除                      | 图层名、状态名或参数名匹配排除列表时，会保留该图层。                                               |
| 状态数量不足                     | 图层中状态数量过少，无法判断默认状态和条件状态。                                                 |
| 没有默认状态                     | 状态机缺少 Default State，无法安全转换。                                              |
| 没有 Entry Transition        | MA Responsive 图层通常依赖 Entry Transition 判断条件，没有可解析的 Entry Transition 时会跳过。 |
| 多状态图层被禁用                   | 图层包含多个条件状态，但关闭了“多状态图层”选项。                                                |
| 存在行为组件                     | 状态机或状态上存在不安全的 StateMachineBehaviour 时会跳过，避免破坏原本逻辑。                       |
| 存在复杂过渡                     | 状态之间存在不支持的跳转、复杂出站过渡或不符合要求的 Any State 结构。                                 |
| Transition 设置不安全           | 例如 Transition Duration 不为 0、Offset 不为 0、Exit Time 不符合要求等。                |
| 状态设置不安全                    | 例如启用了 Speed Multiplier、Motion Time、Mirror、Cycle Offset、Foot IK 等特殊设置。    |
| 条件无法解析                     | 条件参数缺失、条件类型不支持，或无法转换为 BlendTree 可使用的参数范围。                                |
| Int 条件不符合要求                | 例如使用了 `NotEqual`，或者 `Equals` 的阈值不是整数。                                    |
| Bool / Int 参数无法安全迁移        | 如果某个 Bool / Int 参数同时被其他保留图层使用，并且无法安全改写条件，相关图层会被保留。                       |
| NDMF / Modular Avatar 版本过旧 | 缺少 MA2BT Pro 需要的 API 时，会跳过所有优化，并在 Inspector 中显示错误提示。                     |

如果某个图层被跳过，可以在 Unity Console 中查看 `[MA2BT Pro]` 输出的具体原因。


## 鸣谢

MA2BT Pro 基于 Null-K/MA2BT 修改和扩展，感谢原项目作者 PuddingKC / Null-K。

## License

Unlicense

本项目可以自由使用、修改和再发布，包括商业用途。
