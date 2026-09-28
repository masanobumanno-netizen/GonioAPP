# Gonio Web 0.5.0 — Windows計測・共有履歴

CONTECから取得した電圧差を、測定ごとの可変2点校正で角度（deg）へ換算するアプリです。Windows PC上のブラウザ画面と.NETブリッジで計測し、共通のサーバーへ履歴・CSV・動画を保存します。Dropbox APIは共有サーバーからのバックアップに使用します。

**現在の導入方法はGitHubのソースをWindowsでビルドする方式です。** 旧v0.4.0の未署名ZIPはWindowsのコード整合性ポリシーでブロックされたため、新規導入には使いません。自動更新用の署名はWindowsのコード署名とは別物です。

## 導入

1. 管理者が[共有サーバーを準備](docs/SHARED-SERVER.md)し、HTTPS URLを決めます。
2. 各Windows PCで[初回導入・更新手順](docs/WINDOWS-LOCAL.md)に従ってビルドします。
3. 初期管理者を作成し、ユーザー管理から利用者を登録します。全PCに同じ共有サーバーURLを設定します。
4. CONTEC公式API-AIO(WDM) x64ドライバを別途インストールし、Device Utilityで機器を登録・診断します。

Dropboxだけで履歴DBを同期する構成ではありません。共有サーバーのDBを全PCから参照し、完成した測定ファイルをDropboxへ送信します。共有サーバー停止中もログイン済みPCで測定を継続でき、停止時にPC内へ保存します。初回・再起動後のログインには共有サーバー接続が必要です。

## 機能

- CONTEC AI-1608AY-USB / AIO-160802AY-USB / AI-1608GY-USB / **AIO-160802GY-USB**。100Hz内部クロック、FIFO取得、±10V、物理ch 0〜7から2chを選択。実機エラー時に模擬値へ切り替えません。
- 45°・90°の姿勢で1〜5秒平均を取得して毎回校正。`deg = 45 + (差電圧 − 45°校正値) × 45 / (90°校正値 − 45°校正値)`。mm換算は行いません。
- 10点移動平均、通常測定・刺激動画連動測定、カメラ録画、一時停止・再開、CSV/JSON/録画のダウンロード。
- ブラウザ途中保存と、停止時のPCディスク保存。15秒ごとの共有サーバーへの再送。データはアップロード後もPC内に保持します。
- 実ユーザー認証、管理者/一般利用者の権限、最終管理者の保護、測定者のサーバー側確定、共通設定、履歴検索、刺激動画ライブラリ管理。
- FFmpegで録画と角度グラフを上下に合成したMP4を生成。状態表示・エラー時の再試行。
- Dropbox OAuth PKCE、暗号化したrefresh token、分割アップロード、失敗時の再試行。接続前も測定は共有サーバーに保持します。

履歴は施設内のログイン済み利用者全員で共有します。施設/組織ごとの分離には別サーバーを使用してください。管理者の非表示操作はソフト削除です。保存ファイルやDropbox上のコピーは削除しません。

## 検証

.NET 8 SDK、Node.js、Python 3を使用します。

```sh
dotnet build apps/shared-server
dotnet build apps/device-bridge
dotnet run --project apps/shared-server -- --self-test
dotnet run --project apps/device-bridge -- --self-test
node --test tests/core.test.mjs
python3 tests/run-shared-integration.py
```

共有統合テストは一時ディレクトリ・空きポートに専用サービスを起動し、合成データだけで検証します。Dropbox自己テストはHTTPを模擬し、実アカウントへアクセスしません。動画テストの実行方法は[検証状況](docs/IMPLEMENTATION-STATUS.md)を参照してください。

## 実機・運用確認が必要な項目

- Windowsのローカルビルド・起動許可、CONTECの電圧・100Hz精度・USB切断復帰。
- 実カメラ/刺激動画/計測の同期精度。開始オフセットは記録しますが、ハードウェア同期ではありません。
- 本番HTTPSサーバー、Dropboxアプリ登録・OAuth接続・実アカウントへの送信、バックアップ復元。

コード署名証明書は未取得です。ローカルビルドも組織のポリシーによって実行を拒否される場合があります。Windowsの保護機能を無効化する手順は含めません。[CONTEC実機確認項目](docs/SDK.md)も参照してください。
