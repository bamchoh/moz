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
type   = "real"      # bool | int | uint | dint | udint | real | lint | ulint | string | time | tod | date | dt | 宣言済みの構造体名
count  = 4           # 配列要素数 (省略: 1)
length = 16          # string のみ: バイト長 (1..250、必須)。他の型では指定するとエラー
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
  | lint | 4 ワード | holding, input | 64bit 符号付き (スクリプトでは BigInt) |
  | ulint | 4 ワード | holding, input | 64bit 符号なし (スクリプトでは BigInt) |
  | string | ceil(length/2) ワード | holding, input | UTF-8、各ワードの上位バイトが先頭の文字、余りは NUL 詰め。読取は最初の NUL で終了、不正な UTF-8 は U+FFFD。length バイトを超える書込みはエラー |
  | time | 2 ワード | holding, input | int32 ミリ秒 (IEC TIME) |
  | tod | 2 ワード | holding, input | uint32 ミリ秒、0 時からの経過 (TIME_OF_DAY) |
  | date | 4 ワード | holding, input | uint64 秒、Unix epoch からの UTC 0 時の時刻 (DATE)。書込みは UTC 0 時へ切り捨て |
  | dt | 4 ワード | holding, input | uint64 秒、Unix epoch からの経過 (DATE_AND_TIME) |
- 時刻型のレジスタ配置は karakuri と同じ (time=int32, tod=uint32, date/dt=uint64)。変更ログは time/tod を整数 ms、date/dt を `2026-11-01T09:30:00Z` (ISO-8601 UTC、西暦 9999 年超は整数のまま)、string を `"..."` (`"` と `\` は `\` でエスケープ) で出力する
- `count > 1` で配列。要素は offset, offset+1, ... に並ぶ (string は要素ごとに ceil(length/2) ワード)
- `type` に `[[struct]]` で宣言した構造体名を書くと構造体変数になる (下記)
- **重複禁止**: 同一 device 内で変数名の重複、および同一 (device, kind) の範囲重複は設定エラー。別 device なら同じ名前・同じ offset でよい
- 変更通知は 1 回の書き込みバッチにつき 1 行。マスタが生レジスタを書いても変数として観測される

## [[struct]] — 構造体

ユーザー定義の構造体型。フィールドは宣言順に語テーブル上へ連続配置される。

```toml
[[struct]]
name = "Motor"          # 必須、一意。組み込み型名 (int 等) は不可

[[struct.field]]
name = "speed"          # 必須、構造体内で一意
type = "int"            # スカラ型名または他の構造体名 (ネスト可、循環は不可)

[[struct.field]]
name = "on"
type = "bool"           # 構造体内の bool は 1 ワード (0 = false、0 以外 = true)

[[struct.field]]
name = "label"
type = "string"
length = 6              # string フィールドは length 必須 (1..250)、他の型では不可

[[struct.field]]
name = "samples"
type = "real"
count = 4               # フィールド配列 (省略: 1)

[[variable]]
name   = "motor"
device = "plc-a"
kind   = "holding"      # 構造体は holding | input のみ (coil/discrete はエラー)
offset = 100
type   = "Motor"        # 語数 = 1 + 1 + 3 + 8 = 13
count  = 2              # 構造体配列: 要素ごとに 13 ワードずつ並ぶ
```

- **展開**: 設定読込時に構造体変数は「葉変数」へ展開され、以降の層 (変数テーブル、スクリプト、変更ログ、OPC UA) は葉だけを見る。葉の名前は `motor.speed`、ネストは `drive.motor.rpm`、構造体配列は `motors[0].on` (0 始まり)。フィールド配列は 1 つの葉に `count` を持つ (`motor.samples`、`readVariable('motor.samples', 2)` で要素アクセス)
- 変更ログは葉ごとに 1 行 (`plc-a motor.speed=7`)。マスタが FC16 で構造体全体を書くと葉の数だけ行が出る
- **検証**: 未知の型、構造体の循環 (`struct cycle through "A"`)、struct 名/field 名の重複、組み込み型名との衝突、フィールド 0 個、string フィールドの length 欠落/範囲外、`count < 1`、ビットテーブル上の構造体変数、構造体変数への `length` 指定、展開後の葉名と他の変数名の衝突 (`duplicate variable name "motor.speed"`)、device 長超過と重複 (展開後の総語数で判定) はすべて設定エラー
- `[[struct.field]]` は `[[struct]]` 直下の入れ子テーブル配列として書く (Tomlyn 2.10.1 で 2 要素以上のバインドを確認済み)
- **並び順の注意**: Tomlyn は同名のテーブル配列を別のテーブルを挟んで再開できない (`[[variable]]` → `[[struct]]` → `[[variable]]` は "Duplicate key" のパースエラー)。`[[struct]]` 群は `[[variable]]` 群の前にまとめて書くこと

## [[server]] — プロトコルサーバ

```toml
[[server]]
name      = "sim-a"   # 必須、一意
protocol  = "modbus-tcp"  # "modbus-tcp" | "modbus-rtu" | "opcua" (opcua は後節)
device    = "plc-a"   # 参照する device 名 (未宣言名はエラー)
bind      = "127.0.0.1"  # TCP。省略: 127.0.0.1
port      = 50200     # TCP。省略: 50200。0 は ephemeral (テスト/CI 用)
unit_ids  = [1]       # 応答する Unit ID/スレーブアドレス。省略/空 = 全部に応答 (0..247)
```

### シリアル (modbus-rtu) 用のキー

```toml
[[server]]
name = "rtu-a"
protocol = "modbus-rtu"
device = "plc-a"
serial_port = "COM10"   # 必須 (modbus-rtu のとき)。"/dev/ttyUSB0" 等も可
baud       = 9600       # 省略: 9600 (110..4000000)
parity     = "even"     # none|even|odd。省略: even (Modbus 規定の 8E1)
data_bits  = 8          # 省略: 8 (7..8)
stop_bits  = 1          # 省略: 1 (1..2)
idle_gap_ms = 50        # RTU フレームのアイドル区切り (省略: 50; 9600bps で 3.5 文字 ≈ 4ms)
```

- `serial_port` は modbus-rtu では必須、それ以外の protocol では設定禁止
- unserved なスレーブアドレスには無応答 (マスタ側はタイムアウト)。詳細は docs/spec-modbus.md の RTU 節

## [[client]] — クライアント (マスタ): リモートスレーブのミラー

moz が Modbus TCP マスタとして動作し、リモートスレーブを周期ポーリングして答えをローカル device にコピーし (`[[client.poll]]`、リモート → ローカル)、ローカル device の変更をリモートへ書き戻す (`[[client.push]]`、ローカル → リモート)。1 つの `[[client]]` は 1 本の持続 TCP 接続を poll と push で共用する。

```toml
[[device]]
name = "plc-mirror"

[[client]]
name        = "poll-plc1"
device      = "plc-mirror"          # 鏡像の置き先 (必須、未宣言名はエラー)
remote_addr = "192.168.0.10:502"    # host:port (必須; hostname も可)
remote_unit = 1                   # 送信する Unit ID (省略: 1, 0..247)
interval_ms = 1000                # ポーリング周期 (省略: 1000, 100..3600000)
timeout_ms  = 5000                # 1 要求あたりのタイムアウト (省略: 5000, 10..600000)

[[client.poll]]
fc            = 3       # 読み取り FC: 1..4 (書き込み FC は対象外)
remote_offset = 100     # リモートの開始アドレス
count         = 10      # 読み取り点数 (ワード FC: 1..125, ビット FC: 1..2000)
local_kind    = "holding"  # 省略可: fc から推測 (fc1→coil, fc2→discrete, fc3→holding, fc4→input)
local_offset  = 0       # ローカル device の開始アドレス

[[client.push]]
fc            = 16      # 書き込み FC: 5 | 6 | 15 | 16 (省略: 16)
local_kind    = "holding"  # 省略可: fc から推測 (fc5/15→coil, fc6/16→holding)。discrete/input を読み取り元にしてもよい
local_offset  = 20      # ローカル device の開始アドレス (この範囲を監視する)
count         = 2       # 点数 (fc5/6: 1 固定、fc15: 1..1968、fc16: 1..123)
remote_offset = 20      # リモートの開始アドレス
```

poll (リモート → ローカル):

- `local_kind` は fc と同じ大きさ (ビット/ワード) でなければエラー。例: リモートの holding をローカルの input として公開する等の用途で指定する
- ミラーは 1 回の poll につき 1 回の device 書き込み (= 1 つの変数変更イベント)
- 接続・タイムアウト・例外応答・解析エラーはすべて WARN ログ 1 行で、次の tick は普通に動く。通信エラーがあってもプロセスは止まらない

push (ローカル → リモート):

- 起動時に全 push を 1 回送る (初期同期: ローカルの現在値をリモートへ)。以降はローカル範囲が変更されるたび (`Watch` 通知) に、ポーリング周期を待たず即座に範囲全体を書く。1 回のローカル変更につき 1 要求、送信中に再度変更されればもう 1 回
- poll が自分で書いたローカル変更は push しない。同じセルを poll と push の両方に書けば双方向ミラーになる (エコーループは起きない)。ただし初期同期はローカル → リモートの向きなので、ローカルが 0 のまま起動するとリモートの値を 0 で上書きする点に注意
- 通信エラー (接続失敗・タイムアウト・不正応答・応答のアドレス/点数不一致) は WARN ログ 1 行、接続を切り、未送達のまま次 tick に再送する。リモートの例外応答は `exc=` 付きの Request ログ 1 行で、再送はしない (次のローカル変更で再試行)
- 要求は `fc` で指定した形そのまま (fc5/6 は 1 点固定の単一書き込み、fc15/16 は複数書き込み)。IndexRange/部分書き込みはない
- 検証: fc が 5|6|15|16、count が fc の上限内、`local_kind` のビット/ワード整合、ローカル範囲が device の長さに収まること (push は毎回ローカルを読むため `push local_offset 20 (+4 cells) exceeds device ... length` で設定エラー)、不明キー

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
| server.baud | 110 .. 4000000 |
| server.data_bits | 7 .. 8 |
| server.stop_bits | 1 .. 2 |
| server.idle_gap_ms | 1 .. 1000 |
| server.unit_ids[i] | 0 .. 247 |
| server.endpoint | opcua のみ必須。`opc.tcp://host:port` (port 1..65535) |
| variable.count | 1 .. |
| variable.length | 1 .. 250 (string のみ、必須) |
| struct.field.count | 1 .. |
| struct.field.length | 1 .. 250 (string のみ、必須) |
| client.remote_unit | 0 .. 247 |
| client.interval_ms | 100 .. 3600000 |
| client.timeout_ms | 10 .. 600000 |
| client.poll[].fc | 1 .. 4 |
| client.poll[].count | ワード FC: 1 .. 125、ビット FC: 1 .. 2000 |
| client.push[].fc | 5, 6, 15, 16 |
| client.push[].count | fc 5/6: 1、fc 15: 1 .. 1968、fc 16: 1 .. 123 |
| client.push[].local_offset + count | device の該当テーブル長以内 |
| script.interval_ms | 100 .. 3600000 |
| log.level | trace/debug/info/warn/error |
| log.format | text/json |
| server.parity | none/even/odd |
| server.protocol | modbus-tcp / modbus-rtu / opcua (他はエラー) |

- device 名・server 名・struct 名はそれぞれ重複禁止（種類が違えば可）
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

## [[server]] protocol = "opcua" — OPC UA サーバ (フェーズ 7)

```toml
[[server]]
name     = "ua-a"
protocol = "opcua"
device   = "plc-a"      # device の型付き変数をノード公開
endpoint = "opc.tcp://127.0.0.1:4840"  # 必須 (opc.tcp URL、bind/port キーは不使用)
```

- ノード構成: `Objects/<device名>/<変数名>` (ns=urn:moz:ua:ns、フォルダの BrowseName=device 名、変数ノードは `s=<変数名>`)。`[[variable]]` 宣言分がすべて公開される
- 構造体変数はフォルダ (FolderType) になり、葉がその配下に並ぶ: `Objects/<device名>/motor/speed` (NodeId は葉の完全名 `s=motor.speed`、BrowseName は末尾の `speed`)。ネストは入れ子のフォルダ、構造体配列は要素ごとのフォルダ `motors/[0]/on` (NodeId `s=motors[0].on`)
- UA データ型の対応: bool→Boolean, int→Int16, uint→UInt16, dint→Int32, udint→UInt32, real→Float, lint→Int64, ulint→UInt64, string→String, time→Int32 (ms), tod→UInt32 (ms), date/dt→DateTime (1970 年より前の書込みは BadOutOfRange、date は UTC 0 時へ切り捨て)
- セキュリティ: None ポリシー + 匿名のみ (karakuri 準拠)。証明書は自己署名を pki-<name> フォルダに自動作成
- **ServerSecurityPolicy.SecurityMode は必須** (SDK 2.0 の既定値は Invalid で、未設定だと全 OPN が黙って失敗する — スパイクで確認済み)
- 読み取りはノード値 (device の Watch で常に最新)、書き込みは VariableTable 経由。ステータス: Variant の型が DataType と違う → BadTypeMismatch、値域外 → BadOutOfRange、input/discrete 上の変数 → BadNotWritable。int/uint ノードの Variant は Int16/UInt16 (DataType と一致)
- device 側の変更は Watch で node.Value へ反映 → サブスクライブ中のクライアントに通知される
- `count > 1` の変数は ValueRank=OneDimension (ArrayDimensions=[count]) の配列ノード。読み書きは配列全体 (要素数が違う書込みは BadOutOfRange、スカラを書くと BadTypeMismatch)。IndexRange による部分書込みは未対応
- SDK は OPCFoundation.NetStandard.Opc.Ua **2.0.1** (1.5.378 は .NET 10 でトランスポートが壊れているため不可 — スパイクで確認済み)

### フェーズ 7 検証状況と既知の注意点

- **外部クライアント検証済み** (asyncua): browse (asyncua のパス表記では `Objects/plc--a/temperature`; `-` がエスケープされる)、read (スクリプト周期書込み値が UA 経由でも追従)、write (`42.5` → device → Modbus マスタへも反映)。in-process SDK クライアントでも read / 全スカラ型の write / device 変更のノード反映を回帰テストで担保
- **タイムスタンプ/ステータス付き書き込み**: asyncua の `write_value()` や UAExpert が送る SourceTimestamp 付き書込みも受理する (値だけ採用、タイムスタンプとステータスは無視)。ノードの AccessLevel/UserAccessLevel に StatusWrite|TimestampWrite を立てていないと SDK が BadUserAccessDenied を返す — 回帰テストで pin
- **クライアント通知**: device 変更 → Watch → node.Value 更新 → publish → in-process クライアントの MonitoredItem に新値が届くまでを回帰テスト済み
- **匿名ユーザーの書込み**: 全ノードが全ユーザーに読み書きを許可する (moz はアクセス制御を持たない)。in-process SDK クライアントの書込みも回帰テスト済み
