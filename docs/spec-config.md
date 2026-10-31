# 設定仕様 (moz.toml)

moz は起動時に 1 つの TOML ファイルを読む。この文書が仕様である。

- パーサ: Tomlyn (厳格モード)。**未知のキーはエラーになる**（typo や将来キーの取りこぼしを防ぐため）
- エラーは 1 件でもなく、検出できた全件が一度に出力される
- 設定エラー時は console に一覧を表示して exit code 2

## 全体

```toml
[log]
level  = "info"       # trace | debug | info | warn | error (省略: info)
format = "text"       # text | json (省略: text)
file   = "moz.log"    # 任意。指定すると JSON lines でファイルにも出力 (absent: console のみ)
```

## [[device]] — シミュレート装置

```toml
[[device]]
name    = "plc-a"     # 必須、一意
coils   = 65536       # Coil 領域の点数 (1..65536、省略: 65536)
discrete = 65536      # Discrete Input 領域
holding = 65536       # Holding Register 領域
input   = 65536       # Input Register 領域
```

device は独立したメモリ空間。複数の server が同一 device を参照できる。

## [[server]] — プロトコルサーバ

```toml
[[server]]
name      = "sim-a"   # 必須、一意
protocol  = "modbus-tcp"  # 現行: "modbus-tcp" のみ
device    = "plc-a"   # 参照する device 名 (未宣言名はエラー)
bind      = "127.0.0.1"  # 省略: 127.0.0.1
port      = 50200     # 省略: 50200。0 は ephemeral (テスト/CI 用)
unit_ids  = [1]       # 応答する Unit ID。省略/空 = 全 Unit ID に応答 (0..247)
```

unit_ids を設定した場合、リストに無い Unit ID への要求は**無応答**（マスタ側はタイムアウトする）。

## 範囲と検証ルール

| キー | 範囲 |
|---|---|
| device 内の点数 | 1 .. 65536 |
| server.port | 0 .. 65535 |
| server.unit_ids[i] | 0 .. 247 |
| log.level | trace/debug/info/warn/error |
| log.format | text/json |
| server.protocol | modbus-tcp (他はエラー) |

- device 名・server 名は互いに重複禁止（種類が違えば可）
- `[[device]]` が 0 件でも `[[server]]` が 0 件でも起動する（何も動かないだけでエラーではない）

## 例

最小構成:

```toml
[[device]]
name = "plc1"

[[server]]
name = "sim"
protocol = "modbus-tcp"
device = "plc1"
```

2 台のサーバ + ログファイル:

```toml
[log]
level = "debug"
file = "moz.log"

[[device]]
name = "plc-a"
holding = 1000
coils = 1000

[[server]]
name = "sim-a"
protocol = "modbus-tcp"
device = "plc-a"
port = 50200
unit_ids = [1]

[[server]]
name = "sim-b"
protocol = "modbus-tcp"
device = "plc-a"
port = 50201
```