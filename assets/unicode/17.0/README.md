# Unicode emoji 数据

版本固定为 17.0；数据日期 2025-08-04。运行时不联网，也不跟随 latest。

- 数据来源：https://www.unicode.org/Public/17.0.0/emoji/emoji-test.txt
- 文件：./emoji-test.txt
- SHA256：1D8A944F88D7952F7EF7C5167FEF3C67995BCAE24543949710231B03A201ACDA
- 许可：./LICENSE.txt，Unicode License V3；来源 https://www.unicode.org/license.txt
- 许可文件 SHA256：E7A93B009565CFCE55919A381437AC4DB883E9DA2126FA28B91D12732BC53D96

接受 fully-qualified、minimally-qualified、unqualified 行的完整序列，拒绝 component 行。资格变体映射只用于配置去重，不改动实际 token，不给任意会话正文做规范化。○ 为单独兼容项。

数据与许可内嵌到 EXE，加载时核对数据哈希。合法序列不等于当前系统字体一定完整支持；输入时显示真实本机预览和提示，明确渲染失败不添加。