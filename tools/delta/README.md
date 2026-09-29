# xdelta3 更新组件

`xdelta3.exe` 取自 [jmacd/xdelta v3.2.1 Windows x86_64 正式附件](https://github.com/jmacd/xdelta/releases/tag/v3.2.1)。
原始 ZIP 的 SHA-256 为 `8754db3a662156d0c13dcef140d94b36b2a5aadd6aa6b4f65bf30ff036724924`；
本目录 EXE 的 SHA-256 为 `2081fb24a7b8a89d068b58e7d9353647d5fc1512a68bb6a37f71fc60ca84d943`。
版权和 Apache 2.0 授权见 [LICENSE](LICENSE)。

发布脚本和客户端使用同一份工具。正式补丁用 `-9 -S lzma` 生成；客户端只在核对正式清单和旧包哈希后应用，重建后的 EXE 必须与完整发布包的 SHA-256 相同。
