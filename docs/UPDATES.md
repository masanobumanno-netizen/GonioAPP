> v0.5.0の現行導入は[Windowsソースビルド](WINDOWS-LOCAL.md)です。この文書はコード署名証明書取得後に再開するバイナリ配布用で、現時点では使用しません。

# 自動更新とリリース

配信リポジトリは masanobumanno-netizen/GonioAPP。公開Releasesのlatestにある update-manifest.json を参照する。GitHubトークンはクライアントへ同梱しない。非公開リポジトリは未対応。

## 利用者

初回はInstall-Gonio.cmdでユーザー領域に導入し、以後はデスクトップショートカットから起動する。起動後に最新版を確認・取得し、次回起動時に切り替える。ポータブルのStart-Gonio.cmdでは更新しない。アプリ・ランチャーが動いている間はバージョンを切り替えない。起動失敗した候補は保留し旧版を起動する。

画面の履歴はブラウザのIndexedDB、表示設定はlocalStorage、生電圧は従来のstreamsフォルダ。アップデートはこれらを変更しない。ブラウザとURLを変えると別の履歴になる。将来の保存形式変更は前方・後方互換性を別途確認する必要がある。

## 配信側

1. アプリとランチャーのVersion、Program.csのhealth versionを更新する。互換性を壊すランチャー自体の更新は初回導入し直しが必要（現行ランチャーは安定した起動・更新部分）。
2. .NET 8 SDKでアプリとランチャーをwin-x64のself-containedでpublishする。
3. Windowsで `scripts/sign-windows.ps1` を実行してAuthenticode署名と検証を行う（`docs/WINDOWS-SIGNING.md`参照）。その後 `python3 scripts/package-windows.py --dotnet <dotnet実行ファイル> --key <署名秘密鍵>` を実行する。秘密鍵はこの作業環境の `.release-keys/update.private.pem` に作成済み。Git・配布ZIPに含めない。同じ鍵を安全にバックアップし、次回以降も使う。GitHubへ鍵をアップロードしない。
4. テスト後に `sh scripts/publish-release.sh` で指定リポジトリへバイナリ・署名manifestだけ公開する。新しいバージョン番号を使い、既存リリースは上書きしない。

公開鍵はupdate-config.jsonに固定。manifestはRSA/SHA-256署名済みペイロード（base64）と署名を含み、バージョン・GitHub資産URL・サイズ・SHA-256を署名で拘束する。HTTPSのみで取得、展開前にハッシュ確認、ZIPのパストラバーサル・重複・シンボリックリンク・サイズ上限を検証する。

自動更新の切替はstate.jsonを原子的に保存し、候補は独立ディレクトリへ展開する。確認前のクラッシュはtrial.jsonから復帰。起動確認は専用ランダムinstance値とバージョンをhealthで照合し、別プロセスを誤認しない。既に4173が使われていれば切替も停止処理も行わない。ドライバ更新は対象外。

## 検証範囲

Macで署名・改ざん拒否・配信元拒否・破損ZIP・パストラバーサル・不完全パッケージ・バージョン相違・state保存をテスト。Windows x64へクロスビルド。
Windows実機で未検証: ショートカット作成、旧ZIP版からの引継ぎ、実リリースのダウンロードから次回起動での適用、起動失敗時復旧、CONTEC実機取得。公開配信に接続したWindowsでこれらの受入試験が必要。

## 既知の問題（2026-09-28）
v0.4.0のGonioLauncher.dllは未署名のため、VerifiedAndReputableDesktopポリシーにより0x800711c7でブロックされることをユーザーのWindowsで確認。更新manifestの署名では解消しない。公開信頼されたコード署名証明書の用意が必要。署名済み修正版はまだ作成していない。今後のパッケージ作成ではWindowsでAuthenticode検証を必須とする。
