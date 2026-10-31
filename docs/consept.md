# moz — 構想と設計

moz は PLC の「通信」をシミュレートするツールであり、Modbus TCP サーバ（スレーブ側）を設定ファイルから複数起動する CLI です。

karakuri（先代ツール）の反省を設計指針にしています:

- **プロセス分離プラグイン (gRPC) は作らない**: デバッグが複雑化した。プロトコルは同一プロセス内の独立した名前空間 + インターフェースで分離する
- **深いレイヤー化はしない**: karakuri の `internal/domain|application|infrastructure` は人間に追跡できなくなった。moz は小さなパッケージ数・ファイルを一度に読めるサイズに保つ
- **仕様を文書化する**: `docs/spec-*.md` に実装仕様を書き、テストがそれをエンコードする

## 3 層構成

```
┌─────────────────────────────────────────────────┐
│ Moz.Cli (moz)                                   │
│  引数解析 / ログ接続 / 設定→実装の唯一の factory   │
└──────────────┬──────────────────────────────────┘
               │ MoZ.IProtocolRunner (Runner 一覧)
┌──────────────▼──────────────────────────────────┐
│ Moz.Core                                        │
│  ┌─ Modbus.Mbap + Modbus.Tcp ── トランスポート層 │
│  │    MBAP フレーミング / TCP listen              │
│  ├─ Modbus.ModbusPdu ── コーデック層 (純関数)     │
│  │    Handle(pdu, device) → 応答 pdu             │
│  └─ Devices.IDeviceAccess ── 記憶装置 seam       │
│       coil / discrete / holding / input の 4 領域│
│       + 変更通知 Watch                            │
└─────────────────────────────────────────────────┘
```

依存は常に上から下へ。`Moz.Modbus` が参照してよいのは `Moz.Devices` と `Moz.Logging` だけで、設定や CLI を参照しない。

- **Devices 層**: シミュレート装置のメモリ空間。全プロトコル・将来の OPC UA サーバ・GUI が消費する唯一の表面が `IDeviceAccess`。型付き名前付き変数はこの上のオーバーレイとして後のフェーズで追加する
- **コーデック層 (ModbusPdu)**: request PDU → メモリ操作 → response PDU を純関数で実装。ソケットがなくても単体テスト可能で、TCP/RTU/ASCII で共有される
- **トランスポート層**: MBAP + TCP。RTU はこの層だけ置き換える（CRC16 + 3.5 文字アイドル + シリアル）。クライアント（マスタ）役もこの層に dial 側として追加する

## 将来の拡張と seam

| 拡張 | 入る場所 | 先行層への影響 |
|---|---|---|
| Modbus RTU (シリアル) | `Modbus.Rtu` 新 Runner (phase 5) | なし (codec 共有) |
| クライアント (マスタ) | `Modbus` にリクエスト構築半分 + 新 Runner (phase 6) | なし |
| OPC UA | OPCFoundation.NetStandard を **in-process** で `IDeviceAccess` を消費 (phase 7)。変数をノード公開、変更通知は `Watch` を利用 | なし |
| GUI (.NET) | `Moz.Cli` と並列の別プロジェクト。コア = `IDeviceAccess` + `Watch` + `ILoggerProvider` の追加で購読 | なし |
| スクリプト | コアの上にRunner として追加 (phase 4) | なし |

## ログ

通信イベントは構造化ログとして slog（Microsoft.Extensions.Logging）に一本化する。`CommLog` が EventId を集中管理し、text/JSON の両フォーマッタへ同じ意味を供給する。将来の GUI は独自の `ILoggerProvider` を追加するだけで通信ログを購読できる。karakuri に存在しなかった通信エラーの体系的な記録はここに含まれる（malformed frame, 接続エラー, listener 失敗など）。

## anti-scope（あえて作らないもの）

- プラグインローダ・マニフェスト・gRPC / AssemblyLoadContext 動的ロード
- DI コンテナ / リフレクション探索（factory switch は CLI の 1 箇所）
- config ホットリロード（プロセス再起動で対応）
- GUI・REST API・レジスタ編集 UI（別フェーズ）
- TLS・認証・DB・トリミング/AOT
- OpenTelemetry/メトリクス（通信ログに統一）

## 実装フェーズ

1. Modbus TCP サーバ N 台 + 通信ログ ← **完了**
2. 仕様の強化 (`moz validate`、MBAP ポリシーマトリクスをテストで pin) ← **完了**
3. 型付き名前付き変数 (`[[variable]]`、bool〜ulint、変更ログ) ← **完了**
4. スクリプト (Jint, `plc` API, ULINT=BigInt) ← **完了**
5. Modbus RTU (シリアル; Stream 汎用サーバ、メモリ pipe で結合テスト) ← **完了**
6. クライアント (マスタ; `[[client]]` ポール表、リモートをローカル device へミラー) ← **完了**
7. OPC UA (in-process C#) ← **現在地**