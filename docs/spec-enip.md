# EtherNet/IP (CIP) 実装仕様 (フェーズ 23: タグサーバ)

この文書は `Moz.Core/EtherNetIp/` の仕様であり、`tests/Moz.Tests/EnipServerTests.cs` (CIP ルータ・カプセル化・ランナー) と `EnipConfigTests.cs` (設定) がこの内容をエンコードしている。両者を同時に更新すること。

moz は EtherNet/IP の **明示メッセージ (explicit messaging)** を TCP (実機は 44818) で受け、ControlLogix / CompactLogix / Omron NJ と同じ **タグ名による読み書き** に応える **サーバ** (`[[server]] protocol = "enip"`) である。タグは device の `[[variable]]` そのもの (名前・型・要素数)。クライアント (スキャナ) は未実装。サイクリック I/O (クラス 1、UDP) は対象外。pycomm3 / pylogix / libplctag / Ignition を想定し、実機・実クライアントでは未検証。

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
- 応答の CIP 予算: 非接続 504 バイト、接続型は Forward Open の接続サイズ − 2

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
