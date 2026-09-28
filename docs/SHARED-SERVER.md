# 共有サーバーとDropbox APIの設定

## 構成

各Windows PCのGonio Web → HTTPS共有サーバー → SQLite/動画保存 → Dropbox API。
Dropboxはファイルのバックアップ先で、ライブDBやユーザー認証の共有元ではない。共有サーバーは1インスタンスで運用する。SQLiteと処理キューを複数レプリカで同時起動しない。

## Dockerで起動

1. 常時稼働するサーバーにこのリポジトリとDocker Composeを用意する。
2. `.env.example` を `.env` へコピー。`GONIO_SETUP_TOKEN` にランダム32バイト以上の値を設定する。`.env` はGitへ登録せず、管理者だけが読める権限で保存する。
3. `docker compose up -d --build`。ローカルの `http://127.0.0.1:5280/health` で起動を確認する。
4. リバースプロキシを用意し、管理するドメインのHTTPSから5280へ転送する。証明書を有効にし、API・`/dropbox/callback` を転送する。外部公開するポートはHTTPSを使用する。
5. 大きな動画のアップロードに対応するようプロキシの本文上限・タイムアウトを調整する。アプリのリクエスト上限は2GiB（multipartの付加分も含む）。
6. Windows側のユーザー環境変数 `GONIO_SHARED_URL` に `https://実際のホスト名/` を設定する。`.env` の値をWindowsアプリが自動で読むわけではない。
7. Windowsアプリの初期管理者登録で `GONIO_SETUP_TOKEN` を入力する。登録後は同じキーで管理者を追加できない。以後はユーザー管理を使用する。

初回セットアップ・ログインは10回/分の制限がある。プロキシ構成では接続元がプロキシのIPになるため、この制限は利用者間で共有される。多人数の同時ログインが必要なら、信頼するプロキシの明示設定を含めて調整する。

## Dropboxの接続

1. [Dropbox App Console](https://www.dropbox.com/developers/apps)でScoped access / App folderのアプリを登録する。
2. Permissionsで `files.content.write` と `files.metadata.read` を許可し、変更を保存する。
3. OAuth 2のRedirect URIへ `https://実際のホスト名/dropbox/callback` を登録する。
4. `.env` の `DROPBOX_APP_KEY` と `DROPBOX_REDIRECT_URI` を設定し、`docker compose up -d` で反映する。App secretは不要（PKCE）。
5. Gonio Webに管理者でログインし、システム設定からDropbox接続を実行する。Dropbox側で保存先アカウントと権限を確認して許可する。
6. 合成のテスト測定を保存し、履歴のDropbox状態が「完了」になり、Dropboxアプリフォルダ内にCSV・JSON・録画・解析MP4があることを確認する。

保存先はアプリフォルダ内の `/GonioWeb/subjects/{被験者ID由来の名前}-{ハッシュ}/{測定UUID}/`。刺激動画は `/GonioWeb/stimulus/`。同じ測定IDへの再送は同じファイルを上書きする。未接続・認証切れ・容量不足時は共有サーバーのファイルを保持する。測定は自動再試行し、解析エラーは画面から再試行できる。

refresh tokenはサーバー側Data Protectionで暗号化してDBへ保存する。暗号化キーは同じデータボリューム内の `keys` にあるため、ホストのアクセス制限とボリュームの暗号化・バックアップ管理が必要。Dropbox認証情報を各Windows PCへ配布しない。

## 保持と復旧

- Docker named volume `gonio-shared` 内の `/data` にDB・測定・刺激動画・暗号化キーを保存する。
- バックアップは共有サーバーを停止してから `/data` 全体をコピーする。稼働中にSQLite本体だけをコピーしない。`keys` が失われるとDropboxの再認証が必要になる。
- 更新は計測のない時間帯にバックアップ、`git pull --ff-only`、`docker compose up -d --build`。`docker compose down -v` はデータを削除するため使わない。
- 復元は停止状態でボリューム全体を戻し、権限を維持して起動する。復元テストを本番導入前に行う。
- アップロード中断・解析中断は再起動後に再処理する。Windowsの送信待ちは元の測定者が再ログインすると再送する。
- PCのブラウザ途中保存は約3秒ごと。停止操作時に録画・JSON・CSVをディスクに書き出す。計測中のOSクラッシュや電源断に対する完全な動画復旧は保証しない。
- データは自動削除しない。ディスク容量を監視する。管理者の「非表示」は保存ファイルやDropbox上のコピーを物理削除しない。

本番サーバーURLとDropbox登録情報は未設定。Dockerでの起動・合成データ検証と、実アカウント/実機での稼働確認は別段階として扱う。
