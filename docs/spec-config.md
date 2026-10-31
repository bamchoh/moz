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

## [[variable]] — 型付き名前付き変数

生メモリに型と名前を与えるオーバーレイ。変更はログに `plc-a temperature=12.5` 形式で出力される。

```toml
[[variable]]
name   = "temperature"
device = "plc-a"     # 参照する device 名 (未宣言名はエラー)
kind   = "holding"   # coil | discrete | holding | input
offset = 0           # 0ベースの開始位置
type   = "real"      # bool | int | uint | dint | udint | real
count  = 4           # 配列要素数 (省略: 1)
```

- 型とサイズ（レジスタはビッグエンディアン）:
  | type | サイズ | 置ける kind | 値域 |
  |---|---|---|---|
  | bool | 1 点 | coil, discrete | true/false |
  | int | 1 ワード | holding, input | -32768..32767 |
  | uint | 1 ワード | holding, input | 0..65535 |
  | dint | 2 ワード | holding, input | 32bit 符号付き |
  | udint | 2 ワード | holding, input | 32bit 符号なし |
  | real | 2 ワード | holding, input | IEEE754 単精度 |
- `count > 1` で配列。要素は offset, offset+1, ... に並ぶ
- **重複禁止**: 同一 device 内で変数名の重複、および同一 (device, kind) の範囲重複は設定エラー。別 device なら同じ名前・同じ offset でよい
- 変更通知は 1 回の書き込みバッチにつき 1 行。マスタが生レジスタを書いても変数として観測される
- 構造体・STRING[n]・TIME/DATE 型は OPC UA フェーズで追加する

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

## [[script]] — 周期スクリプト

```toml
[[script]]
name        = "counter"
device      = "plc-a"   # 対象 device (必須、未宣言名はエラー)
path        = "scripts/counter.js"  # config からの相対パス (必須)
interval_ms = 500       # 100..3600000 (省略: 1000)
```

スクリプトの書き方 (`plc` オブジェクト、BigInt 扱い) は docs/spec-script.md が仕様。

## 範囲と検証ルール

| キー | 範囲 |
|---|---|
| device 内の点数 | 1 .. 65536 |
| server.port | 0 .. 65535 |
| server.unit_ids[i] | 0 .. 247 |
| script.interval_ms | 100 .. 3600000 |
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