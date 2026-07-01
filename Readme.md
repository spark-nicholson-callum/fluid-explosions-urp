# 流体シミュレーションの爆発エフェクト
Unity 6.4 (URP) 環境向けに独自実装した、リアルタイム流体シミュレーションによる爆発エフェクトです。
コンピュートシェーダーによる流体計算、レイマーチングを用いたボリューム描画、カスタムレンダーパスによるポストプロセスなど、すべてのパイプラインをフルスクラッチで構築しています。
また、モバイル向けに高度な最適化を施しており、数世代前のスマートフォン（Pixel 6にて検証）でも、複数のエフェクトを同時に60fpsで安定して描画可能なパフォーマンスを実現しています。

![All Examples](Examples/URP-All.gif)

合わせて、新たなレンダーパスに独自のポストプロセスを追加することで、より爆発感を演出できます。

![Flash](Examples/URP-Flash3.gif)

## 論文

処理の説明についての論文が作成中ですが、今までの物ご覧いただけます：

* [描画について](rendering.md)
* [データ操作について](particleSystem.md)
* [最低制限の数学](math.md)

## Examples

![Fire Pillar](Examples/URP-Flame.gif)
![Sparks](Examples/URP-Sparks.gif)
![Explosion](Examples/URP-Explo.gif)
![Flash](Examples/URP-Flash3.gif)
![Mushroom](Examples/URP-Mushroom3.gif)
