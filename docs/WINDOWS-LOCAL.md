# Windows Codexでの初回導入・更新

配信元: https://github.com/masanobumanno-netizen/GonioAPP

## 初回

1. 公式Gitと.NET 8 SDKを導入する。
2. `git clone https://github.com/masanobumanno-netizen/GonioAPP.git` でソースを取得する。
3. 共有サーバーのHTTPS URLを環境変数 `GONIO_SHARED_URL` に設定する。永続設定を使う場合はユーザー環境変数として設定後、新しいターミナルを開く。
4. `scripts/windows/Build-Local.ps1 -Start` を実行する。
5. デスクトップの「Gonio Web Local」で起動する。既存のGonio Webを終了して4173を空けておく。
6. 共有サーバーのアカウントでログイン。CONTEC公式x64ドライバを別途導入し、Device Utilityで機器を登録する。

ローカルビルドであっても、組織のアプリ制御ポリシーで実行できる保証はない。ブロックされた場合は保護機能を無効にせず、管理者が許可する配布方式を相談する。

## 更新依頼

> GonioAPPのGitHubの最新版に更新してください。計測中なら作業を止めてください。未保存のソース変更と測定データ・設定を保持し、scripts/windows/Update-Local.ps1を使ってビルド・自己テストを実行してください。保護設定は変更せず、起動確認までお願いします。

更新スクリプトは変更がある作業ツリーを拒否し、fast-forwardのみ使用する。ビルドと自己テスト成功後だけ起動先を変更し、古いビルド・データを残す。共有サーバーも別途更新する必要がある。

## データ

- 100Hz生電圧: `%LOCALAPPDATA%\GonioWeb\streams`
- 共有サーバー送信待ち: `%LOCALAPPDATA%\GonioWeb\outbox`
- 途中保存と旧履歴: 同じブラウザのIndexedDB
- ソースビルド: `%LOCALAPPDATA%\GonioWeb\local-builds`

ブラウザURLは引き続き `http://127.0.0.1:4173/`。旧ブラウザ履歴は削除しない。旧版データの共有登録は管理者ログインで内容を確認して実施する。再起動後の送信には元の測定者のログインが必要。
