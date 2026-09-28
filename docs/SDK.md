# CONTEC実装・検証メモ

提供SDK: lzf826aiowdm_931f.zip / API-AIO(WDM) 9.31。ReadmeのAI-1608AY-USB対応とDevelop.cab内のC# P/Invoke定義を確認。公式ドライバは配布ZIPに同梱しない。

## 実装済み

`ContecNative.cs` が公式C#宣言に合わせたDLL境界、`Acquisition.cs` が実機制御、`Program.cs` がローカルHTTP/WebSocket・生電圧保存を担当する。

- AioQueryDeviceNameで列挙（10006で終了）、AioInitで登録名を開き、AY/GY系4型番以外は拒否。
- シングルエンド・±10V固定。選択した最大物理チャンネル+1個をスキャンし、Ch1/Ch2の2列を取り出す。物理番号は0〜7で重複不可。
- デバイスバッファ転送、FIFO、内部クロック10,000µsを設定して読戻し。リピートは公式仕様の1に固定（0.3.0の0設定を修正）。ソフト開始・コマンド停止。状態・メモリを初期化して開始。
- AioGetAiSamplingCountとAioGetAiSamplingDataExで未読データをまとめて取得。UIタイマーを実測クロックに使わない。
- エラー状態、非有限電圧、不正な取得数、5秒の無受信、送信滞留で停止。Stop/Exitと排他ロックの解放を行う。
- 生電圧を順次CSV保存、終了理由を別JSON保存。OS強制終了・電源断時の完全保存は保証しない。
- `--test-device` は統合テスト専用でUIの実機接続からは拒否される。通常起動では模擬へのフォールバックなし。

## 参照した公式資料

- [クロックの単位と設定範囲](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/analog_input/clock/AioSetAiSamplingClock.htm)
- [入力レンジ](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/analog_input/range/AioSetAiRangeAll.htm)
- [AI-1608AY-USBの対応関数](https://help.contec.com/pc-helper/api-tool-wdm/jp/mergedProjects/CAIO/reference/usable_function/ad/AI-1608AY-USB.htm)
- [電圧配列と取得数](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/analog_input/getting_data/AioGetAiSamplingDataEx.htm)
- [FIFOの読み出し](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/tutorial/high_spec_ai/fifo_memory.htm)
- [デバイス列挙](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/common/AioQueryDeviceName.htm)

## Windows実機で残る確認

1. x64公式ドライバ、Device Utility登録名・診断、配線・入力仕様を確認する。
2. 一定の既知電圧を入力し、Utilityとアプリの電圧・チャンネル・極性を比較する。
3. 45°/90°で校正して両点が再現されるか確認。別セッションで変更した校正値が保存されるか確認する。
4. 長時間取得で期待サンプル数（100/秒）・連番・CSVを確認し、実際のクロック精度も測定する。
5. USB切断、別タブ接続、ブラウザ終了、保存先エラー後の停止・ログ・再接続を確認する。
6. カメラ/刺激動画の開始オフセット、一時停止区間を実測して必要な同期精度を評価する。

Macでビルド・自己テスト・明示的テストデバイスの統合テストは成功。Windows実行とCONTEC実機テストは未実施。

## 0.4.0で追加
AIO-160802GY-USB、AI-1608GY-USB、AIO-160802AY-USBのアナログ入力。全て物理0〜7、シングルエンド、±10V。機種プロファイルから設定し、ドライバから最大チャンネル数も確認する。全機種実機未検証。

- [AY/GY入出力機器の関数表](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/usable_function_by_device/ada/AIO-160802AY-USB.htm)
- [リピート回数1固定](https://help.contec.com/pc-helper/api-tool-wdm/en/mergedProjects/CAIO/reference/function_reference/analog_input/repeat/AioSetAiRepeatTimes.htm)
