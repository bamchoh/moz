# スクリプト仕様 (phase 4)

moz は JavaScript (Jint, 純マネージ) による周期実行スクリプトを備える。この文書が仕様である。

## 設定

```toml
[[script]]
name        = "counter"
device      = "plc-a"     # 対象 device (必須)。raw メモリ + 変数はこの device に限定される
path        = "scripts/counter.js"   # config ファイルからの相対パス (必須)
interval_ms = 500         # 100..3600000。省略: 1000
```

- 起動時にファイルが読めない場合、その script runner は fatal (プロセス停止)。ファイルの中身のエラーは致命的ではない (下記)
- スクリプトの実行順序は保証されない (server と並行)

## 実行モデル

- 1 script = 1 engine (Jint)。エンジンは起動時に作られ、tick ごとに同じエンジンで実行する
- ソースは **IIFE で包まれて**実行される: tick を跨いで `const/let` は持ち越されない。持ち越したい状態は (宣言なしの)`counter = 0` のようなグローバル代入か、`plc` 経由の device 変数に置くこと
- **スクリプトのエラーは致命的ではない**: 例外は WARN ログに1行出て、次の tick は普通に動く
- `console.log(...)` の出力は通信・変数の両ログと同じ体裁の構造化ログ (Information, EventId 2101): `script [名前] メッセージ`

## `plc` オブジェクト

| メソッド | 説明 |
|---|---|
| `plc.readVariable(name, index)` | 変数値を取得。bool / number / **BigInt** を返す |
| `plc.writeVariable(name, value, index)` | 変数へ書き込み。型に応じて範囲チェック・エラー |
| `plc.getVariables()` | 変数名の一覧 (配列)。構造体は葉の名前 (`motor.speed`) で並ぶ |
| `plc.getVariableStatus(name)` | 変数を覆うクライアント連携の状態: `"local"` / `"connected"` / `"disconnected"` (下記) |
| `plc.readBit(area, address)` / `plc.writeBit(area, address, value)` | 1 点読み書き (area: coil) |
| `plc.readWord(area, address)` / `plc.writeWord(area, address, value)` | 1 ワード読み書き (area: holding) |

- `area` は短い名前と karakuri 由来の長い名前の両方を受け付ける: `coil`/`coils`, `discrete`/`discreteInputs`, `holding`/`holdingRegisters`, `input`/`inputRegisters`
- discrete/input への write はエラー (読取専用領域)
- `readVariable` の戻り値は変数型に応じて:
  | 変数型 | JS 型 |
  |---|---|
  | bool | boolean |
  | int, uint, dint, udint, real | number (double) |
  | **lint, ulint** | **BigInt** |
  | string | string |
  | time, tod | number (ミリ秒) |
  | date, dt | Date (UTC) |
- `writeVariable` は逆方向: number を整数変数へ書くと範囲チェックして narrowing。BigInt を lint/ulint へ書く。**64 ビットの誤差は生じない**。string は UTF-8 で length バイトを超えるとエラー

## LINT/ULINT と BigInt

JS の `number` は double なので 64bit 整数を正確に表せない。このため:

- `lint`/`ulint` 型の変数は `plc.readVariable` が **BigInt** を返す。通常の演算子 (`+`, `-`, `<` 等) がそのまま 64 ビット精度で動作する
- BigInt リテラルは `n` 接尾: `0xFFFFFFFFFFFFFFFFn`
- 注意: BigInt と number は JS の仕様上、混ぜられない (`1 + 1n` はエラー)。`Number(bigint)` で変換してから混ぜること

```javascript
// ULINT カウンタを 1 ずつ進める (64 ビット精度)
var n = plc.readVariable('serial') + 1n;
plc.writeVariable('serial', n);
```

## 構造体

構造体変数は設定読込時に葉変数へ展開されるため、スクリプトからは葉の名前でアクセスする: `plc.readVariable('motor.speed')`、`plc.writeVariable('motors[1].on', true)`、フィールド配列は `plc.readVariable('motor.samples', 2)`。構造体をまとめて 1 つのオブジェクトとして読み書きする API はない。

## 時刻型と Date

- `date` / `dt` は `readVariable` が JS の `Date` (UTC) を返し、`writeVariable` は `Date` または epoch ミリ秒の number (`Date.now()` の値) を受け付ける。秒未満は切り捨て、`date` はさらに UTC 0 時へ切り捨てる。1970 年より前 (負の値) はエラー
- `time` / `tod` は number のミリ秒。`tod` は 0..4294967295、`time` は int32 の範囲

```javascript
// 1 秒ごとに dt を現在時刻へ、time を経過ミリ秒へ更新する
plc.writeVariable('stamp', new Date());
plc.writeVariable('elapsed', plc.readVariable('elapsed') + 1000);
```

## クライアント連携の状態: getVariableStatus

`[[client]]` の poll / push 範囲 (docs/spec-config.md) が変数のセルに重なるとき、その変数は「リモートと連携している」。`plc.getVariableStatus(name)` はその連携の状態を 1 語で返す。

| 戻り値 | 意味 |
|---|---|
| `"local"` | どのクライアントの poll / push 範囲にも重ならない。書き込みはローカルで完結する |
| `"connected"` | 重なるすべての範囲のクライアントがリモートへ接続中。push 範囲なら書き込みは即座にリモートへ届き、poll 範囲なら値は周期的に更新されている |
| `"disconnected"` | 重なる範囲のクライアントの少なくとも 1 つが未接続。push 範囲への書き込みは未送達として保持され、接続後に送られる。poll 範囲の値は最後に読めた値のまま |

未知の変数名はエラー。典型的な使い方:

```javascript
if (plc.getVariableStatus('setpoint') === 'connected') {
  plc.writeVariable('setpoint', 42);   // いまリモートへ届く
} else {
  console.log('setpoint link is', plc.getVariableStatus('setpoint'));
}
```

## 例: scripts/counter.js

```javascript
// 500ms ごとに temperature を 0.5 度上げる (37.5 で折り返し)
var t = plc.readVariable('temperature') + 0.5;
if (t > 37.5) { t = 20.0; }
plc.writeVariable('temperature', t);
console.log('temperature =', t);
```