# Git Commit Message 规范

本文件仅用于 Git commit message、changelog、PR 提交记录整理等 Git 相关场景。  
在非 Git 场景下，Codex 不需要读取或使用本文件内容，以避免上下文过长。

---

## 适用范围

当任务涉及以下内容时，应使用本规范：

- 生成 commit message
- 修改 commit message
- 批量整理 commit message
- 生成 changelog
- 整理 PR 描述中的提交记录
- 检查提交信息是否符合项目规范

其他代码修改、功能开发、测试、重构任务不需要主动展开本文件。

---

## Commit Message 格式

所有 commit message 必须使用以下格式：

```text
:<type>: (<scope>): <summary> [#issue]
```

示例：

```text
:bug: (server): 修复分类名称引用相等比较导致的误判问题
:sparkles: (tap): 实现 Tap 模型自动选择与三星支持
:zap: (ocr): 优化前台包名检测逻辑，避免识别滞后与空值 #1367
```

---

## 字段说明

### type

`type` 必须使用 Gitmoji 代码形式，不允许直接使用 Unicode emoji。

正确：

```text
:loud_sound: (ocr): 添加前台包名检测的诊断日志
```

错误：

```text
🔊 (ocr): 添加前台包名检测的诊断日志
```

允许使用的 type：

| type | 含义 | 使用场景 |
|---|---|---|
| `:sparkles:` | 新功能 | 新增功能、模块、能力 |
| `:bug:` | 修复问题 | 修复 bug、异常、误判 |
| `:recycle:` | 重构代码 | 调整结构、抽离逻辑、替换实现 |
| `:zap:` | 优化 | 性能优化、体验优化、减少延迟 |
| `:wrench:` | 配置变更 | 新增或修改配置 |
| `:bulb:` | 注释说明 | 补充注释、明确参数含义 |
| `:loud_sound:` | 增加日志 | 添加诊断日志、调试日志 |
| `:mute:` | 移除日志 | 删除冗余日志 |
| `:necktie:` | 业务规则 | 账单、渠道、合并规则等业务逻辑 |

---

### scope

`scope` 表示主要影响模块，必须使用小写英文。

格式：

```text
(<scope>)
```

允许示例：

```text
(bill)
(tools)
(server)
(hooker)
(ui)
(qianji)
(book)
(core)
(service)
(accessibility)
(ocr)
(config)
(tap)
```

规则：

- 使用小写英文。
- 可包含数字或连字符。
- 不使用中文。
- 不使用空格。
- 多模块修改时，选择主要影响模块。
- 无法归类时，优先使用 `(core)`、`(service)` 或 `(config)`。

---

### summary

`summary` 使用中文描述本次变更。

要求：

- 使用中文。
- 使用动词开头。
- 不以句号结尾。
- 简明描述“做了什么”。
- 必要时补充“为什么”。
- 避免空泛描述，例如“修改代码”“优化逻辑”“修复问题”。

推荐动词：

```text
新增
增加
添加
修复
重构
移除
替换
优化
抽离
统一
明确
补充
完善
```

推荐写法：

```text
:bug: (ocr): 修复双击返回键触发 OCR 参数错误
:recycle: (service): 抽离双击背部触发逻辑至独立服务
:zap: (ocr): 优化前台包名检测逻辑，避免识别滞后与空值
```

不推荐写法：

```text
:bug: (ocr): 修复问题
:recycle: (service): 改代码
:zap: (ocr): 优化逻辑
```

---

### issue

如有关联 issue，必须在末尾追加 issue 编号。

格式：

```text
 #<number>
```

示例：

```text
:bug: (hooker): 统一捕获 Throwable 防止 Hook 注册时 Error 泄漏 #1372
:bulb: (qianji): 明确账本布尔及类型参数注释 #1362
:bug: (book): 修复账本获取参数并过滤不可见账本 #1371
```

注意：

- issue 编号前必须有一个空格。
- 不要写成 `(#1372)`。
- 不要写成 `issue #1372`。
- 如果同一变更有 issue，后续修正提交也应继续保留 issue 编号。

---

## 校验正则

推荐校验规则：

```regex
^:(sparkles|bug|recycle|zap|wrench|mute|loud_sound|bulb|necktie): \([a-z][a-z0-9-]*\): .{2,60}( #[0-9]+)?$
```

---

## 重复提交规则

避免生成完全重复的 commit message。

不推荐：

```text
:recycle: (ocr): 替换 OCR 引擎为 Ncnn PP-OCRv5 并重构处理器
:recycle: (ocr): 替换 OCR 引擎为 Ncnn PP-OCRv5 并重构处理器
```

推荐拆分为更具体的描述：

```text
:recycle: (ocr): 接入 Ncnn PP-OCRv5 OCR 引擎
:recycle: (ocr): 重构 OCR 处理器初始化流程
:recycle: (ocr): 调整 OCR 识别结果解析逻辑
```

---

## 常见变更对应规则

### 新增功能

```text
:sparkles: (<scope>): 新增<功能描述>
:sparkles: (<scope>): 添加<模块或能力>
:sparkles: (<scope>): 实现<功能名称>
```

### 修复问题

```text
:bug: (<scope>): 修复<问题现象>
:bug: (<scope>): 修复<原因>导致的<问题>
```

### 重构代码

```text
:recycle: (<scope>): 重构<模块或逻辑>
:recycle: (<scope>): 抽离<逻辑>至<模块>
:recycle: (<scope>): 替换<旧实现>为<新实现>
```

### 优化逻辑

```text
:zap: (<scope>): 优化<逻辑>，避免<问题>
:zap: (<scope>): 优化<流程>，提升<效果>
```

### 日志调整

```text
:loud_sound: (<scope>): 添加<场景>诊断日志
:mute: (<scope>): 移除<模块>冗余日志
```

### 配置调整

```text
:wrench: (config): 新增<模块>配置
:wrench: (config): 调整<配置项>默认值
```

### 注释说明

```text
:bulb: (<scope>): 补充<参数或逻辑>注释
:bulb: (<scope>): 明确<参数>含义
```

---

## Codex 生成 commit 时的行为要求

当 Codex 需要生成 commit message 时：

1. 先根据代码变更判断 type。
2. 再判断主要影响模块作为 scope。
3. 用中文生成 summary。
4. 如果任务、issue、PR 或上下文中出现 `#数字`，则保留在末尾。
5. 不要使用 Unicode emoji。
6. 不要生成英文 summary。
7. 不要生成句号结尾的 summary。
8. 不要生成重复 commit message。
9. 不要使用未列入允许表的 type，除非用户明确要求。

---

## Codex 输出要求

当用户要求“生成 commit message”时，只输出 commit message 本身，不要解释。

当用户要求“分析 commit message”时，可以输出：

- 格式是否符合规范
- 不符合项
- 推荐修正
- 可用的最终 commit message

当用户要求“批量整理 commit message”时，输出修正后的 commit message 列表。

---

## 推荐 AGENTS.md 引用方式

如果希望 Codex 在需要时再读取本文件，可以在 `AGENTS.md` 中只保留以下简短说明：

```md
## Git 提交规范

Git commit message 规范已拆分到 `COMMIT_MESSAGE.md`。

仅当任务涉及生成、分析、修改、批量整理 commit message，或生成 changelog / PR 提交记录时，才读取并遵守该文件。
```
