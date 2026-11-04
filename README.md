# moz

PLC の通信をシミュレートするツール。設定ファイル 1 つから Modbus TCP/RTU・三菱 MC プロトコル サーバ（スレーブ側）・Modbus TCP/RTU・三菱 MC プロトコル・OPC UA クライアント（マスタ側のミラー）・OPC UA サーバ・周期 JavaScript スクリプトを同一プロセスで起動する CLI アプリです。

karakuri ([bamchoh/karakuri](https://github.com/bamchoh/karakuri)) の再構築作です。設計方針と層構成は [docs/consept.md](docs/consept.md)、仕様は [docs/](docs/) を参照してください。

## ビルド / 実行

必要要件: .NET 10 SDK

```bash
# テスト実行
dotnet test Moz.slnx

# 実行 (設定ファイルを渡す)
dotnet run --project src/Moz.Cli -- moz.toml

# 単一実行ファイルのビルド (自前配布)
dotnet publish src/Moz.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

## 使い方

1. 設定ファイル `moz.toml` を用意する (書式: [docs/spec-config.md](docs/spec-config.md))

```toml
[[device]]
name = "plc1"

[[server]]
name = "sim"
protocol = "modbus-tcp"
device = "plc1"
port = 50200
```

2. 検証してから起動する

```bash
moz validate moz.toml   # 問題があれば一覧を表示して exit 2
moz moz.toml
```

3. Modbus TCP (ポート 50200, Unit ID 任意) で接続して読み書きする。Ctrl+C で終了

`[[variable]]` で型付き変数を宣言し、`protocol = "opcua"` の `[[server]]` を足すと同じ変数が OPC UA ノード (`Objects/<device名>/<変数名>`) として公開される。同梱の `moz.toml` がその全部入りの例

## 実装状況 (フェーズ)

- [x] Modbus TCP サーバ N 台 + 通信ログ
- [x] `moz validate` コマンド (設定検証、単一の検証経路)
- [x] 型付き名前付き変数 (`[[variable]]`, 変更ログ)
- [x] スクリプト (JavaScript, 64bit は BigInt で正確)
- [x] Modbus RTU (シリアル、仮想 COM 無しのメモリ pipe テスト込み)
- [x] クライアント (マスタ; `[[client]]`, リモートスレーブのミラー + `[[client.push]]` による書き戻し、`plc.getVariableStatus` で連携状態)
- [x] OPC UA (in-process; 変数→ノード公開、配列ノード、サブスクリプション通知)
- [x] 文字列・時刻型 (`string`/`time`/`tod`/`date`/`dt`) と構造体 (`[[struct]]`、葉変数へ展開)
- [x] 変数の初期値 (`[[variable]] value = ...`、構造体はテーブルで)
- [x] RTU マスタ (`[[client]] protocol = "modbus-rtu"`、TCP と同じ poll/push)
- [x] クライアントの汎用化 (`IRemoteDevice`) と MC プロトコル (3E バイナリ TCP) マスタ (`protocol = "mc-3e"`、`remote = "D100"`)
- [x] MC プロトコル サーバ (`[[server]] protocol = "mc-3e"`、`[[server.map]]` で D/M/X 等をメモリに割り当て)
- [x] オムロン FINS サーバ (`[[server]] protocol = "fins-tcp" | "fins-udp"`、メモリエリア読出し / 書込み / 複数読出し、`[[server.map]]` で DM/CIO/W/H/A/EM をワードテーブルに割り当て)
- [x] OPC UA クライアント (`[[client]] protocol = "opcua"`、ノード ↔ 型付き変数、周期 Read + Write; サブスクリプションは今後)
- [x] 起動順 (サーバの準備完了後にクライアント/スクリプト) と OPC UA サーバのユーザー管理 (`[[server.user]]`、匿名の write/read/deny)
- [x] OPC UA セキュア通信 (`security = ["none", "sign", "aes256-sha256-rsapss:sign-encrypt"]` など RSA 系 7 ポリシー (OPC UA 1.05 の `rsa-dh-aesgcm` / `rsa-dh-chachapoly` を含む)、`accept_untrusted` と pki 信頼リスト; クライアント側も選択可)

## ライセンス

MIT (LICENSE 参照)