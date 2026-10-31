# moz

PLC の通信をシミュレートするツール。Modbus TCP サーバ（スレーブ側）を設定ファイルの通りに複数起動する CLI アプリです。

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

## 実装状況 (フェーズ)

- [x] Modbus TCP サーバ N 台 + 通信ログ
- [x] `moz validate` コマンド (設定検証、単一の検証経路)
- [x] 型付き名前付き変数 (`[[variable]]`, 変更ログ)
- [x] スクリプト (JavaScript, 64bit は BigInt で正確)
- [ ] Modbus RTU (シリアル)
- [ ] クライアント (マスタ)
- [ ] OPC UA (in-process)

## ライセンス

MIT (LICENSE 参照)