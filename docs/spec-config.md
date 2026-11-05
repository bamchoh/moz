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

メモリは 4 テーブル: `bits` / `words` (読み書き) と `read_only_bits` / `read_only_words` (サーバ経由では書けない)。名前はプロトコル中立で、Modbus はコイル / 入力ステータス / 保持レジスタ / 入力レジスタを、MC と FINS はデバイス・エリアをこの 4 つに写す (フェーズ 20 で coils / discrete / holding / input から改名、旧名はエラー)。

```toml
[[device]]
name    = "plc-a"     # 必須、一意
bits   = 65536       # 読み書きできるビット領域の点数 (1..65536、省略: 65536)
read_only_bits = 65536      # 読み取り専用ビット領域 (サーバ経由では書けない。スクリプト・変数・クライアントのミラーからは書ける)
words = 65536       # 読み書きできるワード領域
read_only_words   = 65536       # 読み取り専用ワード領域
```

device は独立したメモリ空間。複数の server が同一 device を参照できる。

## [[variable]] — 型付き名前付き変数

生メモリに型と名前を与えるオーバーレイ。変更はログに `plc-a temperature=12.5` 形式で出力される。

```toml
[[variable]]
name   = "temperature"
device = "plc-a"     # 参照する device 名 (未宣言名はエラー)
kind   = "words"   # bits | read_only_bits | words | read_only_words
offset = 0           # 0ベースの開始位置
type   = "real"      # bool | int | uint | dint | udint | real | lint | ulint | string | time | tod | date | dt | 宣言済みの構造体名
count  = 4           # 配列要素数 (省略: 1)
length = 16          # string のみ: バイト長 (1..250、必須)。他の型では指定するとエラー
value  = [1.0, 2.0, 3.0, 4.0]  # 初期値 (省略可)。起動時にメモリへ書き込まれる
```

- 型とサイズ（レジスタはビッグエンディアン）:
  | type | サイズ | 置ける kind | 値域 |
  |---|---|---|---|
  | bool | 1 点 | bits, read_only_bits | true/false |
  | int | 1 ワード | words, read_only_words | -32768..32767 |
  | uint | 1 ワード | words, read_only_words | 0..65535 |
  | dint | 2 ワード | words, read_only_words | 32bit 符号付き |
  | udint | 2 ワード | words, read_only_words | 32bit 符号なし |
  | real | 2 ワード | words, read_only_words | IEEE754 単精度 |
  | lint | 4 ワード | words, read_only_words | 64bit 符号付き (スクリプトでは BigInt) |
  | ulint | 4 ワード | words, read_only_words | 64bit 符号なし (スクリプトでは BigInt) |
  | string | ceil(length/2) ワード | words, read_only_words | UTF-8、各ワードの上位バイトが先頭の文字、余りは NUL 詰め。読取は最初の NUL で終了、不正な UTF-8 は U+FFFD。length バイトを超える書込みはエラー |
  | time | 2 ワード | words, read_only_words | int32 ミリ秒 (IEC TIME) |
  | tod | 2 ワード | words, read_only_words | uint32 ミリ秒、0 時からの経過 (TIME_OF_DAY) |
  | date | 4 ワード | words, read_only_words | uint64 秒、Unix epoch からの UTC 0 時の時刻 (DATE)。書込みは UTC 0 時へ切り捨て |
  | dt | 4 ワード | words, read_only_words | uint64 秒、Unix epoch からの経過 (DATE_AND_TIME) |
- 時刻型のレジスタ配置は karakuri と同じ (time=int32, tod=uint32, date/dt=uint64)。変更ログは time/tod を整数 ms、date/dt を `2026-11-01T09:30:00Z` (ISO-8601 UTC、西暦 9999 年超は整数のまま)、string を `"..."` (`"` と `\` は `\` でエスケープ) で出力する
- `count > 1` で配列。要素は offset, offset+1, ... に並ぶ (string は要素ごとに ceil(length/2) ワード)
- `type` に `[[struct]]` で宣言した構造体名を書くと構造体変数になる (下記)
- **初期値 `value`** (省略可): 起動時、どのサーバ・スクリプト・クライアントも動く前にメモリへ書き込まれる (変更ログは出ない)。省略した変数は 0 / false / 空文字列のまま。bits/read_only_bits/words/read_only_words のどのテーブルにも書ける (読み取り専用テーブルの「センサ値」を仕込む用途)
  - スカラ: TOML の値 1 つ。`count > 1` なら要素数ちょうどの配列 (`[1, 2, 3]`)
  - bool は `true`/`false`、整数型は TOML 整数 (`0xFFFF` も可) で型の値域内、real は整数か小数、string は length バイト (UTF-8) 以内
  - time/tod は整数ミリ秒、date/dt は TOML 日時 (`2026-11-01T09:00:00Z`; オフセット無しのローカル日時は UTC とみなす) か Unix epoch からの整数秒。date は 0 時 UTC へ切り捨て。1970 年より前はエラー
  - 構造体: フィールド名をキーにしたテーブル (`value = { speed = 10, on = true }`)。ネストはテーブルの入れ子、フィールド配列は配列、構造体配列は要素数ちょうどのテーブル配列 (`[{ speed = 1 }, { speed = 2 }]`)。書かなかったフィールドは 0 のまま、未知のフィールドはエラー
  - 型・値域・要素数の誤りは `moz validate` が `variable.value.speed expects an integer in -32768..32767 (got 70000)` の形で報告する
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
kind   = "words"      # 構造体は words | read_only_words のみ (bits/read_only_bits はエラー)
offset = 100
type   = "Motor"        # 語数 = 1 + 1 + 3 + 8 = 13
count  = 2              # 構造体配列: 要素ごとに 13 ワードずつ並ぶ
value  = [{ speed = 10, on = true, label = "m0" }, { samples = [1.0, 2.0, 3.0, 4.0] }]  # 初期値 (省略可、要素ごとのテーブル)
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
protocol  = "modbus-tcp"  # "modbus-tcp" | "modbus-rtu" | "opcua" | "mc-3e" | "fins-tcp" | "fins-udp" | "kv-hostlink" | "s7" | "enip" | "enip-nj" (opcua / mc-3e / fins / kv / s7 / enip は後節)
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

### MC プロトコル (mc-3e) サーバ

```toml
[[server]]
name     = "mc-a"
protocol = "mc-3e"       # 3E フレーム・バイナリ・TCP (docs/spec-mc.md)
device   = "plc-a"
bind     = "127.0.0.1"   # 省略: 127.0.0.1
port     = 5010          # 省略: 50200

[[server.map]]           # MC デバイス → メモリの割り当て (省略可)
device = "W"             # デバイスコードの英字だけ (D, M, X, Y, W, R, ZR, ...)
kind   = "words"       # bits|read_only_bits|words|read_only_words。ビットデバイスはビットテーブル、ワードデバイスはワードテーブル
offset = 1000            # W0 ↔ words[1000]
count  = 100             # W0..W99 (省略: offset からテーブルの残り全部)
```

- 既定の割り当て: `D → words 0..`、`M → bits 0..`、`X → read_only_bits 0..` (各テーブルの全長)。`[[server.map]]` に同じコードを書くと置き換わり、他の既定は残る。割り当てのないデバイスへの要求は終了コード C05B
- 検証: デバイスコードが既知、kind の種別がデバイスに一致、`offset + count` (count 省略時は残り全部、0 セルならエラー) がテーブル長以内、同じコードの重複、不明キー。`map` は mc-3e / fins 以外の protocol では設定禁止
- `unit_ids` は MC には無く、無視される。終了コードやフレーム規則は docs/spec-mc.md

### FINS (fins-tcp / fins-udp) サーバ (フェーズ 19)

```toml
[[server]]
name     = "fins-a"
protocol = "fins-tcp"    # "fins-tcp" (ノードアドレス交換あり) | "fins-udp" (1 データグラム = 1 フレーム) (docs/spec-fins.md)
device   = "plc-a"
bind     = "127.0.0.1"   # 省略: 127.0.0.1
port     = 9600          # 省略: 50200 (FINS の標準は 9600 なので明示する)
node     = 10            # fins-tcp のサーバノード 1..254 (省略: bind の末尾オクテット、0 / 255 なら 1)。fins-udp では不要

[[server.map]]           # FINS エリア → メモリの割り当て (省略可)
device = "CIO"           # DM | CIO | W | H | A | E0..E18 (EM バンク、16 進)
kind   = "words"       # words | read_only_words (エリアはワード構成なのでワードテーブルだけ)
offset = 1000            # CIO 0 ↔ words[1000]
count  = 100             # CIO 0..99 (省略: offset からテーブルの残り全部)
```

- 既定の割り当ては `DM → words 0..` (テーブル全長) だけ。他のエリアは `[[server.map]]` が無ければ終了コード 1101。ビットアクセス (`CIO 100.05`) は写し先ワードの中のビット
- 検証: エリアが既知、`kind` が words / read_only_words、範囲と重複は mc-3e と同じ。`node` は fins 以外の protocol では設定禁止、fins-udp では無視
- `unit_ids` は無視される。コマンド・終了コード・FINS/TCP の枠は docs/spec-fins.md

### KV ホストリンク (kv-hostlink) サーバ (フェーズ 21)

```toml
[[server]]
name     = "kv-a"
protocol = "kv-hostlink"  # キーエンス KV 上位リンク、ASCII 行プロトコル、TCP (docs/spec-kv.md)
device   = "plc-a"
bind     = "127.0.0.1"    # 省略: 127.0.0.1
port     = 8501           # 省略: 50200 (実機は 8501 なので明示する)

[[server.map]]            # KV デバイス → メモリの割り当て (省略可)
device = "MR"             # DM | EM | FM | W | ZF (ワード) | R | MR | LR | B (ビット)
kind   = "bits"           # ワードデバイスは words | read_only_words、リレーは bits | read_only_bits
offset = 1000             # MR0 (ch 0 bit 00) ↔ bits[1000]; リレーは channel × 16 + bit
count  = 1600             # 100 チャネル分 (省略: offset からテーブルの残り全部)
```

- 既定の割り当ては `DM → words 0..` と `R → bits 0..` (各テーブル全長)。他のデバイスは `[[server.map]]` が無ければ `E0`。同じコードを書けば既定を置き換える
- 検証: デバイスが既知、`kind` の種別がデバイスに一致、範囲と重複は mc-3e と同じ。`node` は設定禁止 (fins 専用)
- `unit_ids` は無視される。コマンド・書式・エラー応答は docs/spec-kv.md

### Siemens S7 (s7) サーバ (フェーズ 22)

```toml
[[server]]
name     = "s7-a"
protocol = "s7"           # S7comm (S7-300/400 クラシック) over ISO-on-TCP (docs/spec-s7.md)
device   = "plc-a"
bind     = "127.0.0.1"    # 省略: 127.0.0.1
port     = 102            # 省略: 50200 (実機は 102 なので明示する。Windows では 102 に管理者権限は不要)

[[server.map]]            # S7 エリア → メモリの割り当て (省略可)
device = "M"              # DB1..DB65535 | M | I (E) | Q (A)
kind   = "words"          # words | read_only_words (S7 はバイト単位だがワードテーブルに写す: バイト b ↔ words[b/2])
offset = 1000             # MB0 ↔ words[1000] の上位バイト、MW0 = words[1000]
count  = 128              # 256 バイト分 (省略: offset からテーブルの残り全部)
```

- 既定の割り当ては `DB1 → words 0..` (テーブル全長) だけ。M / I / Q と他の DB は `[[server.map]]` が無ければ項目エラー 0A。`DB1` を書けば既定を置き換える
- 検証: エリアが既知 (`DB0` や `T` はエラー)、`kind` がワードテーブル、範囲と重複は mc-3e と同じ。`node` は設定禁止 (fins 専用)
- ラック / スロット (TSAP) は検査しないので、クライアント側はどの値でもよい。`unit_ids` は無視される。PDU・項目エラー・SZL は docs/spec-s7.md

### EtherNet/IP (enip / enip-nj) タグサーバ (フェーズ 23, 24)

```toml
[[server]]
name     = "enip-a"
protocol = "enip"         # EtherNet/IP 明示メッセージ、Logix 風のタグサーバ (docs/spec-enip.md)
device   = "plc-a"        # この device の [[variable]] がそのままタグになる
bind     = "127.0.0.1"    # 省略: 127.0.0.1
port     = 44818          # 省略: 50200 (実機は 44818 なので明示する)

[[server]]
name     = "nj-a"
protocol = "enip-nj"      # 同じカプセル化で Omron NJ/NX 風 (W506): CIP STRING、BOOL 2 バイト、Tag Name Server 等
device   = "plc-a"
port     = 44819          # 同じ device を両方言で出すなら別ポート
```

- `[[server.map]]` は無い (タグ = 変数)。変数が無い device でもサーバは立つ (タグ 0 個)
- `enip` は Rockwell Logix 風 (pycomm3 / pylogix 向け)、`enip-nj` は Omron NJ 風 (aphytcomm 向け)。サービス・データ形式・オブジェクトの違いは docs/spec-enip.md。`unit_ids` は無視される

## [[client]] — クライアント (マスタ): リモートスレーブのミラー

moz がマスタ (クライアント) として動作し、リモート機器を周期ポーリングして答えをローカル device にコピーし (`[[client.poll]]`、リモート → ローカル)、ローカル device の変更をリモートへ書き戻す (`[[client.push]]`、ローカル → リモート)。1 つの `[[client]]` は 1 本のリンク (TCP 接続またはシリアルポート) を poll と push で共用する。

`protocol` でプロトコルを選ぶ。poll / push の動き (周期、未送達の保持、エコー抑止、エラー時の再試行) は全プロトコル共通で、**リモート側のアドレス指定だけがプロトコルごとに違う**: Modbus は `fc` + `remote_offset`、MC プロトコルは `remote = "D100"` のようなデバイス表記、OPC UA は `node` + ローカル変数名。無関係なプロトコルのキーを書くとエラーになる。

| protocol | 相手 | 接続キー | poll / push のリモート指定 | ローカル側 |
|---|---|---|---|---|
| `modbus-tcp` (省略時) | Modbus TCP スレーブ | `remote_addr`, `remote_unit` | `fc`, `remote_offset` | `local_kind`, `local_offset`, `count` |
| `modbus-rtu` | Modbus RTU スレーブ (シリアル) | `serial_port` ほかシリアルキー, `remote_unit` | `fc`, `remote_offset` | 同上 |
| `mc-3e` | 三菱 MC プロトコル 3E フレーム (バイナリ, TCP) | `remote_addr`, `network`, `pc`, `module_io`, `module_station` | `remote` (デバイス表記) | 同上 |
| `opcua` | OPC UA サーバ | `endpoint` | `node` (ノード ID) | `variable` (宣言済み変数名; count/local_* は書かない) |

```toml
[[device]]
name = "plc-mirror"

[[client]]
name        = "poll-plc1"
protocol    = "modbus-tcp"          # "modbus-tcp" (省略時) | "modbus-rtu" | "mc-3e" (下記)
device      = "plc-mirror"          # 鏡像の置き先 (必須、未宣言名はエラー)
remote_addr = "192.168.0.10:502"    # host:port (modbus-tcp では必須; hostname も可)
remote_unit = 1                   # Modbus のみ: 送信する Unit ID / スレーブアドレス (省略: 1, 0..247)
interval_ms = 1000                # ポーリング周期 (省略: 1000, 100..3600000)
timeout_ms  = 5000                # 1 要求あたりのタイムアウト (省略: 5000, 10..600000)

[[client.poll]]
fc            = 3       # Modbus のみ: 読み取り FC 1..4 (省略: 3)
remote_offset = 100     # Modbus のみ: リモートの開始アドレス (省略: 0)
count         = 10      # 読み取り点数 (ワード FC: 1..125, ビット FC: 1..2000)
local_kind    = "words"  # 省略可: fc から推測 (fc1→bits, fc2→read_only_bits, fc3→words, fc4→read_only_words)
local_offset  = 0       # ローカル device の開始アドレス

[[client.push]]
fc            = 16      # Modbus のみ: 書き込み FC 5 | 6 | 15 | 16 (省略: 16)
local_kind    = "words"  # 省略可: fc から推測 (fc5/15→bits, fc6/16→words)。read_only_bits/read_only_words を読み取り元にしてもよい
local_offset  = 20      # ローカル device の開始アドレス (この範囲を監視する)
count         = 2       # 点数 (fc5/6: 1 固定、fc15: 1..1968、fc16: 1..123)
remote_offset = 20      # Modbus のみ: リモートの開始アドレス (省略: 0)
```

### MC プロトコル (mc-3e) マスタ

```toml
[[client]]
name        = "mc-master"
protocol    = "mc-3e"
device      = "plc-mirror"
remote_addr = "192.168.0.20:5000"  # 必須 (host:port)
network     = 0         # 3E フレームのルーティング (省略: 0 / 255 / 1023 (0x03FF) / 0)
pc          = 255
module_io   = 1023
module_station = 0
interval_ms = 1000
timeout_ms  = 1000      # 監視タイマにも使う (250ms 単位に丸め)

[[client.poll]]
remote       = "D100"   # デバイス表記: 英字コード + 先頭番号 (X/Y/B/W/SB/SW/DX/DY/ZR は 16 進)
count        = 10       # 点数 (ワードデバイス 1..960、ビットデバイス 1..7168)
local_kind   = "words"   # 省略可: ビットデバイス→bits、ワードデバイス→words。種別が合わないとエラー
local_offset = 0

[[client.push]]
remote       = "M0"
count        = 16
local_offset = 0        # local_kind 省略時 bits
```

- `remote` は mc-3e では必須、Modbus では設定禁止。逆に `fc` / `remote_offset` / `remote_unit` は mc-3e では設定禁止。`network` 等のルーティングキーも mc-3e 以外では禁止
- 終了コード ≠ 0 (デバイス範囲外など) はリモートの拒否として Request ログに `err=C056` の形で出る。poll は何も書かず、push は再送しない。詳細は docs/spec-mc.md

### OPC UA クライアント (opcua)

リモートのノードを **ローカルの型付き変数** に写す。型・テーブル・オフセット・要素数は `[[variable]]` の宣言から取るので、poll / push には `node` と `variable` だけを書く。

```toml
[[variable]]
name = "ua-temperature"
device = "plc-mirror"
kind = "words"
offset = 70
type = "real"

[[client]]
name       = "ua-client"
protocol   = "opcua"
device     = "plc-mirror"
endpoint   = "opc.tcp://192.168.0.30:4840"   # 必須 (opc.tcp://host:port)
interval_ms = 1000
timeout_ms  = 5000                            # 接続・Read・Write の各サービス呼び出しの上限

[[client.poll]]
node     = "ns=2;s=Temperature"               # "ns=2;s=…" / "i=2258" / "nsu=urn:…;s=…" (名前空間 URI 指定も可)
variable = "ua-temperature"                   # この client の device に宣言された変数 (構造体は葉名 motor.speed)

[[client.push]]
node     = "nsu=urn:moz:ua:ns;s=setpoint"
variable = "setpoint"
```

- 接続は匿名ユーザー・SecurityPolicy None のみ (moz はセキュリティを持たない; サーバが None エンドポイントを出していなければ接続エラー)。クライアント証明書は一時フォルダ (`%TEMP%/moz-ua-client/<client名>`) に自動生成される
- 読み取りは周期 Read (`interval_ms` ごとに poll 項目を 1 ノードずつ Read サービスで読む)。サブスクリプション (MonitoredItem) は未対応で後続フェーズ。push はローカル変数の変更ごとに Write サービス 1 回
- 値の変換は OPC UA サーバ側と同じ規則 (docs/spec-config.md の OPC UA 節の型対応: real ↔ Float、int ↔ Int16、date/dt ↔ DateTime、配列 ↔ 一次元配列 …)。ノードの型が変数型と合わないときは `err=BadTypeMismatch`、値域外は `err=BadOutOfRange` としてリモートの拒否扱い (poll は書かず、push は再送しない)。未知のノード・名前空間は `err=BadNodeIdUnknown`
- `count` / `local_kind` / `local_offset` / `fc` / `remote_offset` / `remote` / `remote_unit` / `remote_addr` は opcua では設定禁止。逆に `node` / `variable` / `endpoint` は opcua 以外では禁止
- `plc.getVariableStatus` の `"connected"` はセッションが確立していること
- 接続失敗 (サーバ未起動、None エンドポイント無し、タイムアウト) は WARN ログ 1 行で次周期に再試行。同じ設定ファイル内の moz のサーバに接続する場合は、起動順 (下記「起動順」) によりサーバの準備完了後にクライアントが動き始めるので起動時の失敗は出ない

### シリアル (modbus-rtu) マスタ

```toml
[[client]]
name        = "rtu-master"
protocol    = "modbus-rtu"
device      = "plc-mirror"
serial_port = "COM11"     # 必須 (modbus-rtu のとき)。"/dev/ttyUSB0" 等も可
baud        = 9600        # 以下 [[server]] のシリアルキーと同じ (省略値も同じ: 9600 / even / 8 / 1 / 50)
parity      = "even"
data_bits   = 8
stop_bits   = 1
idle_gap_ms = 50          # 応答フレームの終端とみなす無通信時間、エラー後に捨て読みする時間
remote_unit = 1
timeout_ms  = 1000        # 応答の先頭バイトを待つ時間 (スレーブ無応答の判定)
```

- `serial_port` は modbus-rtu では必須、modbus-tcp では設定禁止。`remote_addr` はその逆 (modbus-rtu で書くとエラー)
- ポートは起動時ではなく最初の周期で開き、開けなければ WARN ログして次の周期に再試行する (TCP の接続と同じ扱い)。開いたポートは無応答やフレーム誤りでは閉じず、エラー後は `idle_gap_ms` だけ受信を捨ててから次の要求を送る
- `plc.getVariableStatus` の `"connected"` は RTU では「ポートが開いていて直前の要求にスレーブが応答した」の意味 (タイムアウトで `"disconnected"` に戻る)。詳細は docs/spec-modbus.md の RTU マスタ節
- 同じシリアルポートを `[[server]]` と `[[client]]` の両方で開くことはできない (OS がエラーにする)

poll (リモート → ローカル):

- `local_kind` は fc と同じ大きさ (ビット/ワード) でなければエラー。例: リモートの words をローカルの read_only_words として公開する等の用途で指定する
- ローカル範囲 (`local_offset` + `count`) は device の該当テーブル長に収まること (毎 tick 書き込む範囲なので `poll local_offset 0 (+10 cells) exceeds device ... length` で設定エラー)
- ミラーは 1 回の poll につき 1 回の device 書き込み (= 1 つの変数変更イベント)
- 接続・タイムアウト・例外応答・解析エラーはすべて WARN ログ 1 行で、次の tick は普通に動く。通信エラーがあってもプロセスは止まらない

push (ローカル → リモート):

- **push はローカルへの書き込みにだけ反応する**。起動時には何も送らず、push 範囲をリモートから読むこともない (読みたければ同じ範囲に poll を書く)。ローカル範囲が変更されるたび (`Watch` 通知) に、ポーリング周期を待たず即座に範囲全体を書く。1 回のローカル変更につき 1 要求、送信中に再度変更されればもう 1 回
- リモートに接続できない間の変更は未送達として保持され、接続できた時点で (最後の値が) 送られる。スクリプトは `plc.getVariableStatus(name)` で接続状態を見てから書くこともできる (docs/spec-script.md)
- poll が自分で書いたローカル変更は push しない。同じセルを poll と push の両方に書けば双方向ミラーになる (エコーループは起きない)
- 使い分け: **書き込み専用の範囲** は push だけ宣言する (ローカルは最後に書いた値を保持する)。**読み書き両方したい範囲** は poll と push を同じ範囲に宣言する (書けば即 push、リモート側の変化は次の poll で入る。push が拒否されてもローカルは次の poll でリモートの実値に戻る)
- 通信エラー (接続失敗・タイムアウト・不正応答・応答のアドレス/点数不一致) は WARN ログ 1 行、接続を切り、未送達のまま次 tick に再送する。リモートの例外応答は `exc=` 付きの Request ログ 1 行で、再送はしない (次のローカル変更で再試行)
- 要求は `fc` で指定した形そのまま (fc5/6 は 1 点固定の単一書き込み、fc15/16 は複数書き込み)。IndexRange/部分書き込みはない
- 検証: fc が 5|6|15|16、count が fc の上限内、`local_kind` のビット/ワード整合、ローカル範囲が device の長さに収まること (push は毎回ローカルを読むため `push local_offset 20 (+4 cells) exceeds device ... length` で設定エラー)、不明キー
- `local_kind` が read_only_bits/read_only_words (読み取り専用テーブル) でもよい: poll と同様、ミラーはプロセス内部の書込みとして読み取り専用テーブルにも届く (Modbus マスタからの書込みだけが拒否される)

## 起動順

`moz <config>` は **サーバ (`[[server]]`) を先に起動し、すべてのサーバが準備完了 (listen 開始) になってからクライアント (`[[client]]`) とスクリプト (`[[script]]`) を起動する**。同じプロセス内のサーバを相手にする client (自己ポーリング) が起動直後に接続失敗を記録しないための順序。準備完了を 30 秒待っても揃わなければ WARN を出してクライアント/スクリプトを起動する。どれか 1 つのランナーが致命的に失敗 (ポート使用中、シリアルポートが開けない等) すると、残りを止めて exit 1。

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
| variable.value | 型の値域内、配列は count 要素ちょうど、string は length バイト以内 |
| struct.field.count | 1 .. |
| struct.field.length | 1 .. 250 (string のみ、必須) |
| client.protocol | modbus-tcp / modbus-rtu / mc-3e / opcua (他はエラー; 省略: modbus-tcp) |
| client.remote_addr | modbus-tcp / mc-3e で必須、modbus-rtu / opcua では禁止 |
| client.endpoint | opcua で必須 (opc.tcp://host:port)、他では禁止 |
| client.poll[].node / variable | opcua で必須 (node はノード ID 表記、variable は同じ device の葉変数名)、他では禁止 |
| client.poll[].count / local_kind / local_offset (push も同じ) | modbus / mc-3e のみ (省略: 1 / fc か device から / 0)、opcua では禁止 |
| client.serial_port | modbus-rtu で必須、他では禁止 |
| client.baud / parity / data_bits / stop_bits / idle_gap_ms | server の同名キーと同じ範囲 |
| client.remote_unit | 0 .. 247 (Modbus のみ; 他では禁止) |
| client.network / pc / module_station | 0 .. 255 (mc-3e のみ; 他では禁止) |
| client.module_io | 0 .. 65535 (mc-3e のみ; 他では禁止) |
| client.poll[].remote / client.push[].remote | mc-3e で必須 (デバイス表記、番号は 24 ビット内、先頭 + count も 24 ビット内)、Modbus では禁止 |
| client.poll[].fc / remote_offset, client.push[].fc / remote_offset | Modbus のみ; mc-3e では禁止 |
| mc-3e の count | ワードデバイス 1 .. 960、ビットデバイス 1 .. 7168 |
| client.interval_ms | 100 .. 3600000 |
| client.timeout_ms | 10 .. 600000 |
| client.poll[].fc | 1 .. 4 |
| client.poll[].count | ワード FC: 1 .. 125、ビット FC: 1 .. 2000 |
| client.push[].fc | 5, 6, 15, 16 |
| client.push[].count | fc 5/6: 1、fc 15: 1 .. 1968、fc 16: 1 .. 123 |
| client.poll[].local_offset + count | device の該当テーブル長以内 |
| client.push[].local_offset + count | device の該当テーブル長以内 |
| script.interval_ms | 100 .. 3600000 |
| log.level | trace/debug/info/warn/error |
| log.format | text/json |
| server.parity | none/even/odd |
| server.protocol | modbus-tcp / modbus-rtu / opcua / mc-3e / fins-tcp / fins-udp / kv-hostlink / s7 / enip / enip-nj (他はエラー) |
| server.map[].device | 既知の MC デバイスコード (英字のみ) / FINS エリア (DM, CIO, W, H, A, E0..E18) / KV デバイス (DM, EM, FM, W, ZF, R, MR, LR, B) / S7 エリア (DB1..DB65535, M, I, Q)、同一 server 内で一意; mc-3e / fins / kv-hostlink / s7 以外では map 自体が禁止 |
| server.node | 1 .. 254 (fins-tcp / fins-udp のみ; 省略: bind の末尾オクテット) |
| server.map[].kind | bits / read_only_bits / words / read_only_words (デバイスの種別と一致) |
| server.map[].offset + count | device の該当テーブル長以内 (count 省略時は残り全部、1 セル以上) |
| server.anonymous | write / read / deny (opcua のみ; 省略: ユーザー無しなら write、ありなら deny) |
| server.user[].name / password | 必須 (name は一意); opcua 以外では user 自体が禁止 |
| server.user[].access | read / write (省略: write) |
| server.security | none / sign / sign-encrypt / `<policy>:sign` / `<policy>:sign-encrypt` を 1 つ以上、重複なし (opcua のみ; 省略: ["none"]) |
| server.accept_untrusted / pki | opcua のみ (省略: true / 実行ファイル隣の pki-<name>) |
| client.security | server.security と同じ書き方の 1 値 (opcua のみ; 省略: none) |
| client.user / password | 両方そろえて指定 (opcua のみ) |
| client.accept_untrusted / pki | opcua のみ (省略: true / 一時フォルダ) |

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
words = 1000
bits = 1000

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
- セキュリティ: 既定は None ポリシー + 匿名。ユーザー管理は下記「ユーザー管理」、セキュリティポリシーと証明書は「セキュア通信」節。証明書は自己署名を pki フォルダに自動作成
- **ServerSecurityPolicy.SecurityMode は必須** (SDK 2.0 の既定値は Invalid で、未設定だと全 OPN が黙って失敗する — スパイクで確認済み)
- 読み取りはノード値 (device の Watch で常に最新)、書き込みは VariableTable 経由。ステータス: Variant の型が DataType と違う → BadTypeMismatch、値域外 → BadOutOfRange、input/discrete 上の変数 → BadNotWritable。int/uint ノードの Variant は Int16/UInt16 (DataType と一致)
- device 側の変更は Watch で node.Value へ反映 → サブスクライブ中のクライアントに通知される
- `count > 1` の変数は ValueRank=OneDimension (ArrayDimensions=[count]) の配列ノード。読み書きは配列全体 (要素数が違う書込みは BadOutOfRange、スカラを書くと BadTypeMismatch)。IndexRange による部分書込みは未対応
- SDK は OPCFoundation.NetStandard.Opc.Ua **2.0.1** (1.5.378 は .NET 10 でトランスポートが壊れているため不可 — スパイクで確認済み)

### フェーズ 7 検証状況と既知の注意点

- **外部クライアント検証済み** (asyncua): browse (asyncua のパス表記では `Objects/plc--a/temperature`; `-` がエスケープされる)、read (スクリプト周期書込み値が UA 経由でも追従)、write (`42.5` → device → Modbus マスタへも反映)。in-process SDK クライアントでも read / 全スカラ型の write / device 変更のノード反映を回帰テストで担保
- **タイムスタンプ/ステータス付き書き込み**: asyncua の `write_value()` や UAExpert が送る SourceTimestamp 付き書込みも受理する (値だけ採用、タイムスタンプとステータスは無視)。ノードの AccessLevel/UserAccessLevel に StatusWrite|TimestampWrite を立てていないと SDK が BadUserAccessDenied を返す — 回帰テストで pin
- **クライアント通知**: device 変更 → Watch → node.Value 更新 → publish → in-process クライアントの MonitoredItem に新値が届くまでを回帰テスト済み
- **匿名ユーザーの書込み**: ユーザーを宣言しない限り、全ノードが匿名セッションに読み書きを許可する (フェーズ 7 の挙動)。ユーザー管理はフェーズ 15 で追加 (下記)

### ユーザー管理 (フェーズ 15)

```toml
[[server]]
name      = "ua-a"
protocol  = "opcua"
device    = "plc-a"
endpoint  = "opc.tcp://127.0.0.1:4840"
anonymous = "read"       # write | read | deny。省略: ユーザー未宣言なら write、宣言ありなら deny

[[server.user]]
name     = "operator"    # 必須、同一 server 内で一意
password = "op-pass"     # 必須。設定ファイルにそのまま書く (シミュレータ用途; 比較は固定時間)
access   = "write"       # read | write (省略: write)

[[server.user]]
name     = "viewer"
password = "view-pass"
access   = "read"
```

- ユーザーは UserName トークン (パスワードはサーバ証明書で暗号化して送られるので None チャネルでも平文では流れない) で認証する。未知のユーザー・パスワード不一致は ActivateSession が `BadUserAccessDenied` で失敗する
- `anonymous = "deny"` のサーバは Anonymous トークンポリシーを出さないので匿名の ActivateSession は失敗する。`"read"` は読み取りとサブスクリプションのみ
- `access = "read"` のユーザー (および `anonymous = "read"`) の Write は `BadUserAccessDenied` (値は変わらない)。読み取りの可否はノード単位では変えられない (全ノード同じ)
- `user` / `anonymous` は opcua 以外の protocol では設定禁止。ユーザー名の重複、空の name / password、access の不正値は設定エラー
- moz 自身の OPC UA クライアントからユーザーで接続するには `[[client]]` の `user` / `password` (セキュア通信節)

### セキュア通信 (フェーズ 16)

```toml
[[server]]
name     = "ua-a"
protocol = "opcua"
device   = "plc-a"
endpoint = "opc.tcp://127.0.0.1:4840"
security = ["none", "sign", "aes256-sha256-rsapss:sign-encrypt"]   # 出すエンドポイント (省略: ["none"])。sign / sign-encrypt だけなら Basic256Sha256
accept_untrusted = true                        # 省略: true。false なら trusted/certs にある証明書のクライアントだけ受け付ける
pki      = "pki/ua-a"                          # 省略: 実行ファイルの隣の pki-<name>。config からの相対パス
```

- `security` の各値が 1 つのエンドポイントになる。書き方は `none`、`sign` / `sign-encrypt` (Basic256Sha256 の略記)、または `<policy>:sign` / `<policy>:sign-encrypt`。policy は次の RSA 系 7 種 (大文字小文字は区別しない):

  | policy | SecurityPolicy URI | 備考 |
  |---|---|---|
  | `basic128rsa15` | Basic128Rsa15 | 非推奨 (古いクライアントとの接続試験用) |
  | `basic256` | Basic256 | 非推奨 (同上) |
  | `basic256sha256` | Basic256Sha256 | `sign` / `sign-encrypt` の略記はこれ |
  | `aes128-sha256-rsaoaep` | Aes128_Sha256_RsaOaep | |
  | `aes256-sha256-rsapss` | Aes256_Sha256_RsaPss | 推奨 |
  | `rsa-dh-aesgcm` | RSA_DH_AesGcm | OPC UA 1.05 (フェーズ 18)。一時 DH 鍵交換 + AES-GCM |
  | `rsa-dh-chachapoly` | RSA_DH_ChaChaPoly | OPC UA 1.05 (フェーズ 18)。一時 DH 鍵交換 + ChaCha20-Poly1305 |

  1 つ以上、重複不可 (`sign` と `basic256sha256:sign` は同じ値で重複扱い)。`none` を含めなければ平文の接続はできない。どのポリシーも同じ RSA 2048 の自己署名証明書を使う (RSA_DH 系も証明書は RSA で、追加の証明書は作られない)。ECC 系ポリシー (ECC_nistP256 等) は ECC 証明書を別途持つ必要があり未対応。RSA_DH 系は 1.05 対応クライアント (OPC Foundation .NET SDK 2.0 等) でのみ接続できる
- サーバ証明書は自己署名を `<pki>/own` に自動生成する (CN=moz-<name>)。クライアント側は通常「信頼する」操作が必要 (UAExpert 等)
- `accept_untrusted = false` のとき、`<pki>/trusted/certs` に無いクライアント証明書は `BadSecurityChecksFailed` で拒否され、その証明書は `<pki>/rejected/certs` に保存される。そのファイルを `trusted/certs` へ移せば次の接続から受け付ける (再起動不要)。`none` エンドポイントではクライアント証明書を検証しない
- 発行者 (CA) チェーンは `<pki>/issuer/certs`。CRL、証明書の有効期限ポリシー、GDS は未対応
- `[[client]] protocol = "opcua"` 側のキー: `security` はサーバと同じ書き方の 1 値 (`"none"` / `"sign"` / `"aes256-sha256-rsapss:sign-encrypt"` 等; 省略: none; サーバがそのエンドポイントを出していなければ接続エラー `offers no SecurityPolicy ...`)、`user` / `password` (UserName トークン; 片方だけはエラー)、`accept_untrusted` (省略: true)、`pki` (省略: 一時フォルダ `moz-ua-client/<client名>`)。クライアント証明書も自己署名で自動生成される
- `security` / `accept_untrusted` / `pki` は opcua 以外の protocol では設定禁止 (server / client とも)
