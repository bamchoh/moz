# moz — 構想と設計

moz は PLC の「通信」をシミュレートするツールであり、Modbus TCP/RTU・MC プロトコル サーバ（スレーブ側）・Modbus TCP/RTU・MC プロトコル クライアント（マスタ側ミラー）・OPC UA サーバ・周期スクリプトを 1 つの設定ファイルから同一プロセスで起動する CLI です。

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
│       bits / read_only_bits / words / read_only_words の 4 領域│
│       + 変更通知 Watch                            │
└─────────────────────────────────────────────────┘
```

依存は常に上から下へ。`Moz.Modbus` が参照してよいのは `Moz.Devices` と `Moz.Logging` だけで、設定や CLI を参照しない。

- **Devices 層**: シミュレート装置のメモリ空間。全プロトコル・OPC UA サーバ・将来の GUI が消費する唯一の表面が `IDeviceAccess`。型付き名前付き変数 (`VariableTable`) はこの上のフラットなオーバーレイ。構造体は設定読込時 (`VariableLayout`) にドット名の葉変数へ展開され、コア層は構造体を知らない
- **コーデック層 (ModbusPdu)**: request PDU → メモリ操作 → response PDU を純関数で実装。ソケットがなくても単体テスト可能で、TCP/RTU/ASCII で共有される
- **トランスポート層**: MBAP + TCP。RTU はこの層だけ置き換える（CRC16 + 3.5 文字アイドル + シリアル）
- **起動 (`RunnerHost`)**: `IProtocolRunner.Ready` (サーバは listen 後に完了、既定は即完了) を使い、サーバ → 準備完了待ち → クライアント/スクリプトの順に起動する。最初に落ちたランナーで残りを止めて失敗を返す (`Task.WhenAll` は全員が終わるまで戻らないので `WhenAny` で監視)
- **クライアント層 (`Moz.Client`)**: プロトコル非依存の poll/push ループ `ClientPoller` と、プロトコルが実装する `IRemoteDevice` (項目ごとのローカル範囲 + ビット列/ワード列の読み書き)。Modbus は `ModbusRemote` (+ `IMasterLink` で TCP/RTU)、MC プロトコルは `McRemote`、OPC UA は `OpcUaRemote` (ノード ↔ 型付き変数; 型変換はサーバ側の `UaValueCodec` をスクラッチ device 経由で再利用)。新プロトコルのクライアントは `IRemoteDevice` 1 つ + 設定キー + CLI の switch 1 行で足せる

## 将来の拡張と seam

| 拡張 | 入る場所 | 先行層への影響 |
|---|---|---|
| Modbus RTU (シリアル) | `Modbus.Rtu` 新 Runner (phase 5) | なし (codec 共有) |
| MC プロトコル サーバ | `Mc.McServer` Runner + `Mc3EServer` ハンドラ (phase 13) | なし (`IDeviceAccess` を消費、`McMap` で割り当て) |
| FINS サーバ | `Fins.FinsTcpServer` / `FinsUdpServer` Runner + `FinsServer` ハンドラ (phase 19) | なし (`IDeviceAccess` を消費、`FinsMap` で割り当て) |
| KV ホストリンク サーバ | `Kv.KvServer` Runner + `KvHostLink` 行ハンドラ (phase 21) | なし (`IDeviceAccess` を消費、`KvMap` で割り当て) |
| S7 サーバ | `S7.S7TcpServer` Runner (ISO-on-TCP) + `S7Server` PDU ハンドラ (phase 22) | なし (`IDeviceAccess` を消費、`S7Map` で割り当て) |
| クライアント (マスタ) | `Modbus` にリクエスト構築半分 + 新 Runner (phase 6) → phase 12 で `Moz.Client` に汎用化 | なし |
| 新プロトコルのクライアント (MC, OPC UA クライアント, ...) | `IRemoteDevice` 実装 1 つ (phase 12 の `Moz.Mc` が例) + `[[client]]` のキー + `CliApp.BuildRemote` の case | なし (ループ・設定の骨格は共有) |
| 新プロトコルのサーバ (MC サーバ, ...) | `IProtocolRunner` 実装 1 つ + `[[server]]` のキー + CLI の switch | なし (`IDeviceAccess` を消費) |
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
- DB・トリミング/AOT
- TLS・認証は Modbus/MC では対象外。OPC UA サーバのユーザー管理 (フェーズ 15) とセキュリティポリシー (フェーズ 16) は要望により実装
- OpenTelemetry/メトリクス（通信ログに統一）

## 実装フェーズ

1. Modbus TCP サーバ N 台 + 通信ログ ← **完了**
2. 仕様の強化 (`moz validate`、MBAP ポリシーマトリクスをテストで pin) ← **完了**
3. 型付き名前付き変数 (`[[variable]]`、bool〜ulint、変更ログ) ← **完了**
4. スクリプト (Jint, `plc` API, ULINT=BigInt) ← **完了**
5. Modbus RTU (シリアル; Stream 汎用サーバ、メモリ pipe で結合テスト) ← **完了**
6. クライアント (マスタ; `[[client]]` ポール表、リモートをローカル device へミラー) ← **完了**
7. OPC UA (in-process C#; 変数→ノード公開、配列ノード、Watch→値更新→サブスク通知、タイムスタンプ付き書込み) ← **完了**
8. 文字列・時刻型・構造体 (`string`/`time`/`tod`/`date`/`dt`、`[[struct]]` を設定読込時に葉変数へ展開、VariableTable はフラットのまま) ← **完了**
9. クライアントの書き戻し (`[[client.push]]`; ローカル device の `Watch` 通知でリモートへ FC5/6/15/16、poll と同じ接続、自分の poll 書込みはエコーしない、`plc.getVariableStatus` で連携状態を参照) ← **完了**
10. 変数の初期値 (`[[variable]] value = ...`) と poll のローカル範囲検証 ← **完了**
11. RTU マスタ (`[[client]] protocol = "modbus-rtu"`; マスタのトランスポートを `IMasterLink` に分離し TCP/RTU で共有、メモリ pipe で結合テスト) ← **完了**
12. クライアントの汎用化 + MC プロトコル (`Moz.Client.ClientPoller` / `IRemoteDevice`; `[[client]] protocol = "mc-3e"` で 3E フレーム・バイナリ・TCP のマスタ、`remote = "D100"` 表記) ← **完了**
13. MC プロトコル サーバ (`[[server]] protocol = "mc-3e"`; `Mc3EServer` 純粋ハンドラ + `McServer` TCP ランナー、`[[server.map]]` でデバイス → メモリ割り当て、既定 D/M/X) ← **完了**
14. OPC UA クライアント (`[[client]] protocol = "opcua"`; `node` ↔ `variable`、匿名 / None、周期 Read + Write) ← **完了**。サブスクリプション (MonitoredItem による変更通知) は後続フェーズ
15. 起動順 (`RunnerHost`: サーバ → Ready → クライアント/スクリプト) と OPC UA サーバのユーザー管理 (`[[server.user]]` name/password/access、`anonymous`; SDK 2.0 の IdentityRegistry にユーザー名認証を登録、書込み権限はノードマネージャで判定) ← **完了**
16. OPC UA セキュア通信 (`server.security` = none / sign / sign-encrypt (Basic256Sha256)、`accept_untrusted` + `pki` の信頼リスト; クライアント側も `security` / `user` / `password` / `accept_untrusted` / `pki`) ← **完了**
17. OPC UA の RSA 系ポリシー全種 (`<policy>:sign|sign-encrypt`: basic128rsa15 / basic256 / basic256sha256 / aes128-sha256-rsaoaep / aes256-sha256-rsapss; ECC は未対応) ← **完了**
18. OPC UA 1.05 の RSA_DH ポリシー (`rsa-dh-aesgcm` / `rsa-dh-chachapoly`; 同じ RSA 証明書で動く。ECC 系は要望により対象外) ← **完了**
19. オムロン FINS サーバ (`[[server]] protocol = "fins-tcp" | "fins-udp"`; `FinsServer` 純粋ハンドラ (0101 / 0102 / 0104) + TCP (ノード交換) / UDP ランナー、全エリアをワードテーブルへ写す `[[server.map]]`、既定 DM → words) ← **完了**
20. メモリモデルの中立な命名 (`TableKind` = Bits / ReadOnlyBits / Words / ReadOnlyWords、設定キー `bits` / `read_only_bits` / `words` / `read_only_words`、`kind` / `local_kind` / `server.map.kind` の値も同じ、スクリプトの area は `bits` / `readOnlyBits` / `words` / `readOnlyWords`。Modbus 由来の coil / discrete / holding / input は設定でもコードでも廃止。互換なし) ← **完了**
21. キーエンス KV ホストリンク サーバ (`[[server]] protocol = "kv-hostlink"`; ASCII 行プロトコル、`CR` / `CQ` / `?K` / `?M` / `RD` / `RDS` / `WR` / `WRS` / `ST` / `RS`、`.U .S .D .L .H` 書式、`[[server.map]]` で DM/EM/FM/W/ZF/R/MR/LR/B を割り当て、既定 DM → words と R → bits) ← **完了**
22. Siemens S7 サーバ (`[[server]] protocol = "s7"`; ISO-on-TCP + S7comm、Setup Communication (PDU ≤ 960)、Read/Write Var の複数項目 (BIT/BYTE/WORD/DWORD、奇数バイト、ビット)、SZL 0011/001C は CPU 315-2 PN/DP 相当、`[[server.map]]` で DB n / M / I / Q をワードテーブルへ (バイト b ↔ words[b/2])、既定 DB1 → words) ← **完了**