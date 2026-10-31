# Modbus 実装仕様 (フェーズ 1: Modbus TCP)

この文書は `Moz.Core/Modbus/` のコーデック実装の仕様であり、`tests/Moz.Tests/ModbusPduTests.cs` がこの表をエンコードしている。両者を同時に更新すること。

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