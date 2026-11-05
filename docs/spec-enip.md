# EtherNet/IP (CIP) 実装仕様 (フェーズ 23: Logix 風タグサーバ、フェーズ 24: Omron NJ 方言)

この文書は `Moz.Core/EtherNetIp/` の仕様であり、`tests/Moz.Tests/EnipServerTests.cs` (Logix 方言の CIP ルータ・カプセル化・ランナー)、`EnipNjServerTests.cs` (Omron NJ 方言) と `EnipConfigTests.cs` (設定) がこの内容をエンコードしている。これらを同時に更新すること。

moz は EtherNet/IP の **明示メッセージ (explicit messaging)** を TCP (実機は 44818) で受け、**タグ名による読み書き** に応える **サーバ** である。タグは device の `[[variable]]` そのもの (名前・型・要素数)。方言は 2 つで、カプセル化と Forward Open / Close は共通、CIP のサービス・データ形式・オブジェクトが違う:

| `protocol` | 方言 | ルータ | 想定クライアント |
|---|---|---|---|
| `"enip"` | Rockwell ControlLogix / CompactLogix 風 (Symbol / Template オブジェクト、Logix STRING 構造体、Fragmented) | `CipRouter` | pycomm3 / pylogix / libplctag (ab_eip) / Ignition |
| `"enip-nj"` | Omron NJ / NX 風 (W506 第 7 章: Tag Name Server / Variable Object、CIP STRING、Simple Data Segment) | `NjCipRouter` | aphytcomm / libplctag (omron-njnx) / CIPUCMMRead を打つ別の NJ |

クライアント (スキャナ) は未実装。サイクリック I/O (クラス 1、UDP) は対象外。実機・実クライアントでは未検証。以下「タグ = 変数」から「CIP ルータ」までは Logix 方言、末尾の「Omron NJ 方言」の節が `enip-nj`。

## タグ = 変数

| 変数の型 | CIP 型 (コード) | 要素サイズ | 備考 |
|---|---|---|---|
| bool | BOOL (C1) | 1 バイト (00 / 01) | 配列は 1 要素 1 バイト (Logix の DWORD パックとは違う) |
| int / uint | INT (C3) / UINT (C7) | 2 | |
| dint / udint | DINT (C4) / UDINT (C8) | 4 | time は DINT、tod は UDINT |
| real | REAL (CA) | 4 | |
| lint / ulint | LINT (C5) / ULINT (C9) | 8 | date / dt は ULINT (秒) |
| string | STRING 構造体 (ハンドル 0FCE) | 88 (LEN DINT + DATA SINT[82] + 2) | 読出しは 82 バイトで切り詰め、書込みは変数の `length` を超えるとエラー 20 |

- 構造体変数の葉 (`motor.speed`) はその名前のタグ。シンボルセグメントを `.` で連結して解決するので `motor` + `speed` の 2 セグメント (メンバアクセス) でも届く
- `count > 1` の変数は 1 次元配列。要素セグメント (28 / 29 / 2A) で先頭要素を指定できる
- Symbol オブジェクトの属性 2 (型) は アトミック: 型コード | 2000h (配列)、文字列: 8FCE
- 読み取り専用テーブル (`read_only_bits` / `read_only_words`) の変数への書込みは 0F (privilege violation)、属性 10 (external access) は 1

## カプセル化 (`Encap` / `EnipServer`)

ヘッダ 24 バイト (リトルエンディアン): コマンド (2) | データ長 (2; 8192 以下) | セッションハンドル (4) | ステータス (4) | 送信元コンテキスト (8; エコー) | オプション (4)。

| コマンド | 動き |
|---|---|
| NOP (0000) | 応答なし |
| ListIdentity (0063) / ListServices (0004) / ListInterfaces (0064) | セッション不要。Identity 項目 (0C) は Identity 属性 + ソケットアドレス + state 3 |
| RegisterSession (0065) | プロトコル版 1 ならハンドルを割り当てて応答 (版が違えばステータス 69)。1 TCP 接続 1 セッション |
| UnRegisterSession (0066) | 接続を閉じる |
| SendRRData (006F) | セッション検査 (不一致はステータス 64)。CPF: null アドレス (0000) + 非接続データ (B2) の CIP 要求を処理。応答は同じ形 |
| SendUnitData (0070) | CPF: 接続アドレス (A1、O→T 接続 ID) + 接続データ (B1、シーケンス + CIP 要求)。応答は T→O 接続 ID + 同じシーケンス |
| その他 | ステータス 01 |

- ヘッダでない (長さ超過等)・CPF が壊れている・未知の接続 ID → MalformedFrame ログで **無応答で接続を閉じる**
- 応答の CIP 予算: 非接続 504 バイト (enip-nj は 502)、接続型は Forward Open の接続サイズ − 2
- ListIdentity / Identity オブジェクトの中身は方言ごと (`CipIdentity`): enip は vendor 0000 / PLC 000E / product 0001 / revision 20.1 / 名前 `moz <server名>`、enip-nj は後述

## CIP ルータ (`CipRouter`)

要求: サービス (1) | パス長 (1、ワード) | パス | データ。応答: サービス | 80h、00、一般ステータス、拡張ステータス数 (ワード)、拡張ステータス、データ。

**タグ (シンボリックパス)**:

| サービス | 要求データ | 応答データ |
|---|---|---|
| Read Tag (4C) | 要素数 (2; 省略時 1) | 型 (2; 構造体は A0 02 + ハンドル 2) + 要素 |
| Read Tag Fragmented (52) | 要素数 (2) + バイトオフセット (4) | 同上、オフセット以降。予算に入らなければ入る分だけ返してステータス 06 |
| Write Tag (4D) | 型 + 要素数 (2) + 要素 | なし |
| Write Tag Fragmented (53) | 型 + 要素数 + バイトオフセット (4) + 要素 | なし。オフセットは要素境界 |

エラー: 04 不明なタグ / 壊れたパス、08 未対応サービス (Read Modify Write 4E 等)、0F 読み取り専用、13 / 15 データ不足 / 過多、20 パラメータ不正 (オフセット、変数の length 超過)、FF + 2105 要素数 0 または範囲外、FF + 2107 型不一致。

**オブジェクト (論理パス)**:

| クラス | サービス | 内容 |
|---|---|---|
| 01 Identity | 01 Get Attributes All / 0E Get Attribute Single (1..7) | vendor 0000、device type 000E、product code 0001、revision 20.1、status 0、serial 004D4F5A、product name `moz <server名>`。属性 8 以降は 14 |
| 02 Message Router | 0A Multiple Service Packet | 埋め込み要求をそれぞれ処理。応答は個数 + オフセット + 各応答 |
| 06 Connection Manager | 52 Unconnected Send | 埋め込み要求を処理し、その応答をそのまま返す (ルートパスは検査しない) |
| | 54 Forward Open / 5B Large Forward Open | 接続を登録し O→T 接続 ID を割り当てて応答 (T→O ID、シリアル、ベンダ、発信元シリアル、API をエコー)。接続サイズ = パラメータ & 1FF (標準) / & FFFF (Large) |
| | 4E Forward Close | シリアル / ベンダ / 発信元シリアルで接続を削除。見つからなければ 01 + 0107 |
| 64 (プログラム名) | 01 | STRING `moz <server名>` |
| 6B Symbol | 55 Get Instance Attribute List | インスタンス (開始 ID、含む) 以降のタグを ID 順に。属性 1 名前 (UINT 長 + 文字)、2 型、3 / 4 / 5 / 6 UDINT 0、8 次元 (UDINT × 3)、10 external access (USINT)。予算に入らない分があればステータス 06 (クライアントは次の ID から続ける)。他の属性は 14 |
| 6C Template | 03 Get Attribute List / 4C Read Template | ハンドル 0FCE (STRING) だけ: 属性 1 ハンドル、2 メンバ数 2、4 定義サイズ 14、5 構造体サイズ 88。Read Template はメンバ情報 (LEN DINT @0、DATA SINT[82] @4) + 名前 `STRING\0LEN\0DATA\0`。他のハンドルは 05 |
| その他 | | 05 (path destination unknown)、既知クラスの未対応サービスは 08 |

リビジョンを 20 にしているのは、pycomm3 がリビジョン 21 以上でシンボルインスタンス ID によるアクセスに切り替えるため (moz は名前でのアクセスだけ)。

Request ログ: `conn=c1 op=read target=temp qty=1 result=ok` / `err=04` / `err=FF:2105`、`op=write` / `read-frag` / `write-frag` / `multi` (target は最初の要求、qty は個数) / `tags` (target `from=<開始ID>`) / `template` / `identity` / `name` / `forward-open` (target `size=<接続サイズ>`) / `forward-close` / `register` / `unregister` / `list-identity`、未知コマンドは `cmd=XXXX err=01`。

テスト: スカラ・配列・要素・文字列・メンバアクセスの読出し、フラグメント読出しの部分転送、各書込みとエラー、Identity / プログラム名 / 未知クラス、Multiple Service Packet、Symbol 一覧 (pylogix 流と pycomm3 流の属性、部分転送、終端、未対応属性)、Template、Forward Open / Close と Unconnected Send、カプセル化ヘッダ、TCP ランナー (ListIdentity → RegisterSession → 非接続読出し → セッション不一致 → Forward Open → 接続型書込み・読出し → UnRegister、不正ヘッダの無応答クローズ)。クライアント側は手書きのソケットコード。

## Omron NJ 方言 (`protocol = "enip-nj"`、フェーズ 24; `NjCipRouter`)

出典は NJ/NX シリーズ CPU ユニット内蔵 EtherNet/IP ポート ユーザーズマニュアル (W506) の 7-4 (リクエストパス)・7-5 (CIP オブジェクト)・7-6 (変数の読み書きサービス)・7-7 (変数のデータ型) と、aphytcomm / libplctag のクライアント実装 (Tag Name Server / Variable Object の応答レイアウトはマニュアル非公開のためクライアントが読む形に合わせた)。Rockwell 風との差分だけを書く。

**変数 (シンボリックパス)**: 変数名は UTF-8 の ANSI 拡張シンボルセグメント (奇数長はパディング)、構造体メンバは続くシンボルセグメント (moz は `.` で連結した葉変数名で解決)、配列要素はメンバ ID の論理セグメント (28 / 29 / 2A)。**Simple Data Segment** (`80 03` + バイトオフセット UDINT + サイズ UINT) を変数名の後ろに付けると、変数の **イメージ** (下表) の部分アクセスになる (aphytcomm は文字列・配列を常にこれで読み書きする)。要素指定とデータセグメントの併用はパスエラー 04。

| サービス | 要求データ | 応答データ |
|---|---|---|
| 変数読出し (4C) | 要素数 (UINT、**必須**; 無ければ 13) | 型 (USINT) + AddInfo 長 00 + 実データ。構造体 (A0 02 + CRC) は出さない |
| 変数書込み (4D) | 型 + AddInfo 長 (0 以外は 20 + 8021、型違いは 20 + 8022) + 要素数 (UINT) + 実データ | なし |
| 読出し / 書込み + Simple Data Segment | 同上 (要素数は見ない) | 型 + 00 + オフセットからサイズ分のイメージ (STRING は UINT バイト数 + バイト列) |
| 52 / 53 / 4E など | | 08 (NJ に Fragmented と Read Modify Write は無い) |
| 01 Get Attributes All (シンボリックパス) | | Variable Object の属性 (後述) |

**データ形式** (W506 7-7):

| 変数の型 | 型コード | 単体 (スカラ / 1 要素) | 要素指定 + 要素数 n > 1 | 要素指定なしの配列全体 (= イメージ) |
|---|---|---|---|---|
| bool | C1 | 2 バイト: 状態 (00 / 01) + 強制情報 (書込みは 0) | 状態 1 バイト × n | 先頭から 16 要素ずつ WORD に詰める (ceil(count/16) ワード)。要素数は 1 か count |
| int / uint | C3 / C7 | 2 バイト | 要素数分 | 要素数分 (count 以下) |
| dint / udint / real | C4 / C8 / CA | 4 | | |
| lint / ulint | C5 / C9 | 8 | | |
| time / tod / date / dt | 09 / 0B / 08 / 0A (ベンダ固有 *_NSEC) | 8 バイト、ナノ秒 (ms × 10^6、秒 × 10^9) | | |
| string | D0 | UINT バイト数 + UTF-8 (NUL 無し)。書込みは変数の `length` 以下 (超過は 15) | 非対応 (要素ごとに 1 要素ずつ) | イメージは `length` バイト固定・NUL 詰め (要素ごと) |

**エラー** (W506 7-6 の表): 04 パス不正 / 不明な変数、13 / 15 データ不足 / 過多、11 応答が予算 (非接続 502、接続型は接続サイズ) を超える (部分転送は無い)、20 + 8017 要素を持たない変数に要素数 ≠ 1 または要素指定、20 + 8018 要素数 0 / 配列範囲外、20 + 8021 / 8022 書込みの AddInfo 長 / 型違い、20 + 800F セグメント書込みの STRING バイト数がサイズと不一致、20 + 8028 書込めない値、1F + 0102,2103 読み取り専用変数への書込み、1F + 0104,1103 Simple Data Segment がイメージをはみ出す。Error ログは `20:8018` / `1F:0104,1103` の形。

**オブジェクト (論理パス)**:

| クラス | サービス | 内容 |
|---|---|---|
| 01 Identity | 01 / 0E | vendor 002F (Omron)、device type 000C、product code 0665、revision 2.6、status 0、serial 004D4F5A、product name `NJ501-1300` (W506 7-5-2)。インスタンス 0 の Get Attributes All はクラス属性 (revision 1、max instance 1) |
| 06 Connection Manager | 52 / 54 / 5B / 4E | Logix 方言と同じ。ただし接続サイズ > 1994 (NJ の Large_Forward_Open 上限) は 01 + 0109 で拒否 |
| 6A Tag Name Server | 01 | クラス属性: revision 1、max instance = タグ数、instance 数 = タグ数 |
| | 5F Get Instance List | 要求: 開始 ID (UDINT) + 個数 (UDINT) + 種別 (UINT; 1 = システム変数 → 0 件、2 = ユーザー変数 = 全タグ、他は 20)。応答: 個数 (UINT) + 続きあり (USINT) + 00 + エントリ (ID UDINT、以降の長さ UINT、クラス 6B、ID、名前長 USINT、UTF-8 名前、偶数長パディング)。タグ ID は `[[variable]]` の順に 1..n。予算に入らなければそこで切り「続きあり」= 1 |
| 6B Variable Object | 01 (インスタンス n、またはシンボリックパス) | サイズ UDINT (配列は要素サイズ; bool は 2)、型 (配列は A3 + 要素型 + 次元数 1 + pad + 各次元の要素数 UDINT)、8 バイト (STRING の Variable Type Object インスタンス + 0)、ビット番号 0 + pad 3、Variable Type Object インスタンス (STRING 以外 0)、配列は開始添字 0。インスタンス 0 はクラス属性、範囲外は 05 |
| 6C Variable Type Object | 01 | 文字列の `length` ごとに 1 インスタンス (出現順): サイズ、00、D0、00、次元 0、メンバ数 0、0 × 4、CRC 0、名前 `STRING[n]` (長さ + 偶数パディング)、次インスタンス 0、入れ子 0。構造体は無い (葉に展開済み) |
| 02 Message Router / 64 / 6B の Symbol 一覧 / 6C Template | | 05 (Multiple Service Packet は W506 の対象オブジェクトに無い)。既知クラスの他サービスは 08 |

Request ログ: Logix 方言と同じ語彙 (`op=read` / `write` / `identity` / `forward-open` ...) に `op=tags` (target `from=<開始ID>`、qty = 返した数) / `variable` (target = 変数名) / `type` (target = インスタンス) が加わる。Simple Data Segment の qty はバイト数。

テスト (`EnipNjServerTests.cs`): スカラ・時刻型・メンバアクセス・要素数必須、BOOL の 3 形式、配列・STRING・予算超過、Simple Data Segment の読み書きと境界エラー、各書込みとエラー表、未対応サービス / オブジェクト、Identity、Tag Name Server (一覧・続き・予算・システム変数・種別エラー)、Variable Object / Variable Type Object、Forward Open の 1994 上限と Unconnected Send、TCP ランナー (ListIdentity に NJ501-1300、非接続 STRING 読出し)。
