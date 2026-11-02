# Modbus 実装仕様 (フェーズ 1: Modbus TCP / フェーズ 5: Modbus RTU / フェーズ 6: クライアント)

この文書は `Moz.Core/Modbus/` のコーデック実装の仕様であり、`tests/Moz.Tests/ModbusPduTests.cs`（PDU）と `RtuTests.cs`（RTU 層）がこの表をエンコードしている。両者を同時に更新すること。

## 用語

- **PDU**: function code + data。MBAP ヘッダ・Unit ID・CRC を含まない
- **ADU (TCP)**: MBAP ヘッダ (7 バイト) + Unit ID + PDU

## バイト順

レジスタは **big-endian**。word swap / byte swap オプションは意図的に存在しない（実機要件が出た時点で config フラグとして検討）。

## 実装するファンクションコード

| FC | 名称 | 上限 | 備考 |
|----|------|------|------|
| 0x01 | Read Coils | qty 1..2000 | ビット読取。応答はビットパッキング (LSB first) |
| 0x02 | Read Discrete Inputs | qty 1..2000 | 同上、読取専用領域 |
| 0x03 | Read Holding Registers | qty 1..125 | 応答 `fc, bc=2*qty, data` |
| 0x04 | Read Input Registers | qty 1..125 | 同上、読取専用領域 |
| 0x05 | Write Single Coil | 値 0x0000 / 0xFF00 のみ | request を echo |
| 0x06 | Write Single Register | — | request を echo |
| 0x0F | Write Multiple Coils | qty 1..1968 | 応答 `fc, addr, qty` (bc/data なし) |
| 0x10 | Write Multiple Registers | qty 1..123 | 応答 `fc, addr, qty` |

要求 PDU の長さ: FC01..06 = 5 バイト、FC0F/10 = 6 + byteCount バイト。

## 例外コード応答 (`fc|0x80, code`)

| 条件 | code |
|---|---|
| 未知の FC | 01 Illegal Function |
| addr + qty が領域長を超える / 読取専用領域への書き込み | 02 Illegal Data Address |
| qty 0 または上限超過 / FC05 の値が 0x0000・0xFF00 以外 / byteCount 不整合 / PDU 長不一致 | 03 Illegal Data Value |
| 予期しないデバイス失敗（プログラムバグ由来） | 04 Server Device Failure |

設計上の約束: **フレーム処理は決して例外で終わらない**。例外 04 は「落とし穴」(chute) であり、トランスポート層に一箇所だけ置かれ、それ以外の箇所には防御的 try/catch を置かない。

## MBAP ヘッダ処理ポリシー

ヘッダ (7 バイト: tid2, proto2, len2, unit1):

| 状況 | 処理 |
|---|---|
| proto ≠ 0 | WARN ログ + **接続を切断** (応答しない) |
| len < 2 または len > 254 | WARN ログ + 接続を切断 |
| PDU 部が途中で切れた (EOF) | WARN ログ (malformed) + 切断 |
| unit_ids 設定に含まれない Unit ID | WARN ログ + **無応答** (接続は維持)。マスタはタイムアウトする |
| 正常 | PDU を処理し、tid / unit を echo して応答 |

## 通信ログ (CommLog)

全通信イベントは構造化ログ (Microsoft.Extensions.Logging) で出力される:

| EventId | 名称 | レベル | 内容 |
|---|---|---|---|
| 1001 | Request | Debug | `conn unit fc addr qty result=ok / exc=N` |
| 2001 | ConnectionAccepted | Information | `conn remote` |
| 2002 | ConnectionClosed | Information | `conn remote` |
| 2003 | ConnectionError | Warning | `conn remote err` |
| 2004 | MalformedFrame | Warning | `conn remote err` |
| 3000 | ListenFailed | Error | `endpoint err` |

text フォーマッタの表示例:

```
info: moz.server.sim-a[0] listening sim-a on 127.0.0.1:50200
info: moz.server.sim-a[0] conn=c1 remote=127.0.0.1:53124
dbug: moz.server.sim-a[0] conn=c1 unit=1 fc=03 addr=0 qty=10 result=ok
dbug: moz.server.sim-a[0] conn=c1 unit=1 fc=03 addr=0 qty=126 exc=3
warn: moz.server.sim-a[0] conn=c1 remote=127.0.0.1:53124 err=bad MBAP header (protocol id / length field)
```

実デバイスとの仕様差分

- **FC 0x07 (診断) 等、上表以外の FC は例外 01 を返す**（実装しないことをエラーで明示）
- broadcast unit id (0/255) への特別扱いはしない（通常の Unit ID として 247 との違いを設けない。unit_ids で制御）
- TCP 上の複数要求パイプライン (1 接続で複数 tid の同時要求) は**逐次処理**する（並行処理しない。シンプルさ優先。将来的に必要なら並列化）

## Modbus RTU (シリアル) — フェーズ 5

フレーミング: `[slave address(1)] [PDU] [CRC16(2)]`。CRC16 は 0xA001、初期値 0xFFFF、LSB が先に伝送される（検証ベクタ: `010300000001` → `840A`、Modicon 例 `1103006B0003` → `7687`）。

**フレーム検出**（シリアルには境界がないので）:

- 1 バイトずつ蓄積し、FC から導出した要求長 + CRC が成立した時点で応答する（FC01-06 は 8 バイト、FC0F/10 は 8 + byteCount）
- **アイドル間隔（既定 50ms、`server.idle_gap_ms` で設定。9600bps での 3.5 文字 ≈ 4ms）でバッファを捨てる**。理由: CRC不一致・未知 FC・フレーム断片はすべて「成立しないフレーム」として捨てられ、次のフレーム待ちに戻る
- **CRC 不一致 → 無応答**（仕様どおり。マスタはタイムアウトする）
- unserved な slave address → 無応答（TCP の unit_ids と同じ規約）。設定した `unit_ids` に含まれないアドレスは答えない
- サーバは生 `Stream` を受け取る。シリアルポートを開く（System.IO.Ports）のは CLI 側であり、コアは配管を知らない。テストはメモリ内 pipe で実施（仮想 COM 不要）
- TCP と同一の PDU コーデック・例外コード・上限を使う。unit id は MBAP ではなく ADU から来る

## Modbus TCP クライアント (マスタ) — フェーズ 6 / 9

`[[client]]` の動き。poll (FC01-04 の読み取りミラー、フェーズ 6) と push (FC05/06/15/16 の書き戻し、フェーズ 9) が 1 本の持続接続を共用する。

- リクエスト構築: `ModbusClient.BuildReadRequest` (fc + addr2 + qty2)、`BuildWriteRequest` (FC05 `addr2 + FF00|0000`、FC06 `addr2 + value2`、FC15 `addr2 + qty2 + byteCount + bits (LSB 先頭)`、FC16 `addr2 + qty2 + byteCount + words (big-endian)`) — MBAP ヘッダ (トランザクション id は接続内で 1 順増加) + unit id を付けて送信
- 応答解析: `ParseWordResponse` (FC03/04) / `ParseBitResponse` (FC01/02)、`CheckWriteResponse` (FC05/06 は要求のエコー、FC15/16 は `fc + addr2 + qty2`; アドレス・点数の不一致は不整合)。**例外応答 (fc|0x80 + code) は解析せず Request ログ 1 行 (`exc=`)**、poll ならローカルには何も書かず、push なら再送しない。応答の FC エコー・byteCount・tid 一致を検証し、不整合は接続ごと切って次 tick で張り直す
- ミラーは 1 poll = 1 回の device Write (1 つの DeviceChange) — 変数変更ログが自然に出る
- push は起動時に 1 回 + ローカル範囲の `Watch` 通知ごとに 1 要求 (範囲全体)。通知があればポーリング周期を待たず即座に送るが、poll の周期は変わらない。poll が自分で書いたローカル変更は push の対象外 (同じセルの双方向ミラーでエコーしない)。未送達 (通信エラー) は次 tick に再送
- 通信エラーの規約はスクリプト/サーバと同じ: ログして次 tick。プロセスは止まらない
- テスト: moz 自身の TCP サーバを相手に実通信で検証（リモート停止→復旧の再接続、push の未送達再送、poll 書込みがエコーしないことも含む）