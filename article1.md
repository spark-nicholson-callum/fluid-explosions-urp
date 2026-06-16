# 炎の描画まで

## 基本の仕組み

みんなが想像するきれいなオレンジ色の炎や黒い煙は、実は割と単純な仕組みです。
煙の粒子は温度によって色が変わります。熱くなると色が赤→黄色→白へと変化し、光も強くなります。

粒子1個分なら簡単に理解できますが、空間に粒子がいっぱいある場合はどうでしょうか。
密度と温度が一定であれば、これも簡単ですね。
密度は透明度に対応します。密度が高いほど、煙が濃くなります。

それなら、空間を小さなキューブ（ボクセル）に分けて、1ボクセル内の煙の密度と温度を一定とし、
すべてのボクセルを重ねて描画すればいいわけです。

これは「ボリュームテクスチャ」と言います。3次元のテクスチャのことですね。

残る問題は以下の2つです。
1. ポリゴンがないものをどうやってGPUで描画するのか？
2. どうやってボリュームテクスチャを生成するのか？

1つずつ見ていきましょう。

### レイマーチング

普通の描画だとオブジェクトの表面の情報しか必要ありませんが、今回はオブジェクトの中の情報も必要になります。

解決方法は割と簡単です。フラグメントシェーダーでカメラからピクセルに向かって線（レイ）を飛ばし、その線に沿ってオブジェクトの中を少しずつ（一歩ずつ）進んでいきます。

いろいろと最適化はできますが、基本はそれだけです。歩幅（ステップサイズ）と回数を決めて、1歩ずつ進みながらボリュームテクスチャのデータをサンプリング（取得）します。
そして、取得したすべてのデータを重ね合わせて描画すれば終わりです。

### 流体シミュレーション

ここからが今回のメインディッシュです。
煙と熱のエミッター（発生源）を用意し、そこから流体がどう動くのかを物理的にシミュレーションします。

普通のゲームならここまでする必要はないのですが、やることでいろんなメリットがあります。
一番重要なのは、炎がプレイヤーの動きからちゃんと影響を受けられることです。
プレイヤーのアクションによって炎が煽られて強くなったり、動きに合わせてなびいたりできます。
最終的には、環境にあるオブジェクトと相互作用することも可能です。木製のものを加熱したり、燃料を爆発させたりといったこともできるようになります。

## 流体シミュレーションのシェーダー

数学の部分が怖いので最後に記載しましたが、本当は先に読むべき部分です。まあ、読まなくても分かるように頑張って説明しますね。

流体シミュレーションはボクセルごとの処理がまったく同じなので、すべて非同期で並列処理が行えます。
これはGPUにピッタリのタスクなので、コンピュートシェーダーで行います。
使ったことがない人にとっては怖そうな言葉ですが、単に「描画しないシェーダー」という意味です。

基本的に以下の処理を行います：

1. 外部の力の影響を計算する
2. 速度の発散を計算する
3. 発散情報を利用して圧力を計算する（数回反復する）
4. 圧力の影響を速度に加算する
5. 移流の処理を行う

#### 外部の力
これはとても簡単です。みんなが学んだニュートン力学と同じです。
基本的に必要なのは浮力と重力です。
温度が高いほど浮力が強くなり、密度が高いほど重力が強くなります。

```hlsl
// Buoyancy
float buoyancyForce = (heat * Buoyancy) - (smokeDensity * SmokeWeight);
vel.y += buoyancyForce * DeltaTime;
```

ここにある `Buoyancy` と `SmokeWeight` が、シェーダーの外部から設定できるパラメータになります。
物理的に正確な値である必要はありません。

#### 速度の発散
圧力の計算に必要な処理です。圧力計算のループの外に置いたほうが効率がいいので、別のステップに分けています。
微分を計算しますが、ここでは単純に隣り合うボクセルの値の差分だけで計算しています。

```hlsl
uint3 pos = groupThreadId + uint3(1, 1, 1); // Offset for the padding

float3 vL = velCache[GetCacheIndex(uint3(pos + int3(-1,  0,  0)))];
float3 vR = velCache[GetCacheIndex(uint3(pos + int3( 1,  0,  0)))];
float3 vD = velCache[GetCacheIndex(uint3(pos + int3( 0, -1,  0)))];
float3 vU = velCache[GetCacheIndex(uint3(pos + int3( 0,  1,  0)))];
float3 vB = velCache[GetCacheIndex(uint3(pos + int3( 0,  0, -1)))];
float3 vF = velCache[GetCacheIndex(uint3(pos + int3( 0,  0,  1)))];

float dx = BoundsSize.x / float(Resolution);
float div = ((vR.x - vL.x) + (vU.y - vD.y) + (vF.z - vB.z)) / (2.0 * dx);
```

隣のボクセルのデータを取得するため、同じ情報を複数回読み込むことがかなり発生します。
その対策として、今回の実装ではデータをキャッシュするようにしています。詳しくは「最適化」の項目で説明します。

#### 圧力の計算

圧倒的に一番重い処理がこちらです。
ポアソン方程式を解く必要がありますが、解析的に解く（一発で正解を出す）ことはできません。
ですので、反復法を使ってちょっとずつ正解に近づけていく形にします。

基本的に以下の処理を40回ほど繰り返します。
これは「ヤコビ法」と呼ばれる手法です。

```hlsl
float pL = pressureCache[GetCacheIndex(uint3(pos + int3(-1,  0,  0)))];
float pR = pressureCache[GetCacheIndex(uint3(pos + int3( 1,  0,  0)))];
float pD = pressureCache[GetCacheIndex(uint3(pos + int3( 0, -1,  0)))];
float pU = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  1,  0)))];
float pB = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  0, -1)))];
float pF = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  0,  1)))];

float dx = BoundsSize.x / float(Resolution);
float div = DivergenceRead[id];
PressureWrite[id] = (pL + pR + pD + pU + pB + pF - (dx * dx * div)) / 6.0;
```

もちろんもっと速いやり方も存在しますが、これが一番実装しやすい方法ですね。
今回の用途ならこれで十分ですが、より本格的にやるならマルチグリッド法などを実装するべきかと思います。

#### 圧力の力
外部の力と同じように速度に加算します。
ただ、発散は外部の力による影響を受け、圧力はその発散による影響を受けるため、外部の力とは別のステップに分ける必要があります。

```hlsl
float pL = pressureCache[GetCacheIndex(uint3(pos + int3(-1,  0,  0)))];
float pR = pressureCache[GetCacheIndex(uint3(pos + int3( 1,  0,  0)))];
float pD = pressureCache[GetCacheIndex(uint3(pos + int3( 0, -1,  0)))];
float pU = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  1,  0)))];
float pB = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  0, -1)))];
float pF = pressureCache[GetCacheIndex(uint3(pos + int3( 0,  0,  1)))];

float dx = BoundsSize.x / float(Resolution);
float3 vel = VelocityRead[id].xyz;
vel.x -= (pR - pL) / (2.0 * dx);
vel.y -= (pU - pD) / (2.0 * dx);
vel.z -= (pF - pB) / (2.0 * dx);
VelocityWrite[id] = float4(vel, 0.0);
```

#### 移流
理論上、移流は普通のゲームオブジェクトの動きと同じで、位置に速度を加算して終わりのはずです。
ですが、1つ大きな問題があります。

あるボクセルの中身が、きれいに丸ごと別の1つのボクセルに移動するわけがありません。
実際には、ボクセルとボクセルの「間」のような半端な位置に移動することのほうが圧倒的に多いです。

これの解決策として、「どこへ行くか」ではなく、「どこから来たか」を計算します。
遡った位置もボクセルの間になりますが、そこはただのテクスチャサンプリング（線形補間、lerp）で値を取得すれば解決できます。

```hlsl
// +.5 to sample the center of the the voxel
float3 uvw = ((float3(id) + 0.5) / float(Resolution));
float3 uvwVel = VelocityRead[id].xyz / BoundsSize;
float3 prevUvw = uvw - (uvwVel * DeltaTime * SimScale);

float4 advectedSmokeProps = SmokePropRead.SampleLevel(sampler_LinearClamp, prevUvw, 0);
float4 advectedVel = VelocityRead.SampleLevel(sampler_LinearClamp, prevUvw, 0);
```

### エミッターについて
温度と煙のエミッターはとても分かりやすいですね。
単純に、該当するボクセルのデータを上書きすれば大丈夫です。

```hlsl
if (insideEmitter(em, cellWorldPos))
{
    advectedSmokeProps.r = max(advectedSmokeProps.r, em.heat);
    advectedSmokeProps.g = max(advectedSmokeProps.g, em.density);
}
```

また、今回は圧力のエミッターも実装してあります。
圧力は発散を打ち消すように働く仕組みになっているため、圧力を直接いじるのは難しいです。
ですので、エミッターの場所で「発散」の値を上書き（減算）してあげればいいのです。

```hlsl

if (insideEmitter(em, worldPos))
{
    div -= em.expansion;
}
```

### 最適化
まだまだ最適化の余地はありますが、とにかく影響の大きいものを1つだけ行いました。

#### 共有メモリのキャッシュ
隣接するボクセルのデータを取得するタイミングがかなりあります。
例えばスレッドグループが `8 x 8 x 8` の場合、`8 x 8 x 8 x 6 = 3072` 回もテクスチャをサンプリングすることになります。
しかし、実際に必要なデータは `10 x 10 x 10 = 1000` ボクセル分のキューブ内にすべて収まっています。
つまり、平均して同じデータを約3回も重複して読み込んでいることになります！

これを解決するために、まずその1000ボクセル分のデータを一気に読み込んでキャッシュしておきます。
VRAMから直接読むよりも、グループ共有メモリ（SRAM）から読み込むほうが圧倒的に速いので、これで全体的なパフォーマンスが向上します。

### 見た目強化
ここまでのままだと、炎の動きがかなり不自然になってしまいます。大体2つの問題があります。

1. 自然界の空気は少しずつ常に動いており、それが炎に影響を与えます。
2. ボクセルベースでシミュレーションをすると、計算の過程で渦（ボルテックス）がどんどん弱まってしまいます。

1つずつ説明いたします。

#### 環境風
ノイズ関数を使うのが一番ですが、単純なサイン波の風を加えるだけでもかなり見た目が良くなります。

```hlsl
float3 wind;
wind.x = sin(worldPos.y * AmbientWindScale + Time) * cos(worldPos.z * AmbientWindScale + Time);
wind.y = sin(worldPos.z * AmbientWindScale + Time) * cos(worldPos.x * AmbientWindScale + Time);
wind.z = sin(worldPos.x * AmbientWindScale + Time) * cos(worldPos.y * AmbientWindScale + Time);

vel += wind * AmbientWindSpeed * DeltaTime;
```

#### ボルティシティ・コンファイメント（渦度閉じ込め）
空間（グリッド）と時間を細かく区切って計算する都合上、どうしても渦が減衰してしまうという現象が起きます。
この渦巻く感じこそが「すごく流体っぽい」部分なので、どうにかして守りたいところです。そのために、速度の回転（カール）を計算し、失われた渦の力をちょっと無理やり加算して補います。

```hlsl
float wL = vorticityCache[GetCacheIndex(uint3(pos + int3(-1,  0,  0)))];
float wR = vorticityCache[GetCacheIndex(uint3(pos + int3( 1,  0,  0)))];
float wD = vorticityCache[GetCacheIndex(uint3(pos + int3( 0, -1,  0)))];
float wU = vorticityCache[GetCacheIndex(uint3(pos + int3( 0,  1,  0)))];
float wB = vorticityCache[GetCacheIndex(uint3(pos + int3( 0,  0, -1)))];
float wF = vorticityCache[GetCacheIndex(uint3(pos + int3( 0,  0,  1)))];

float dx = BoundsSize.x / float(Resolution);
float3 eta = float3(wR - wL, wU - wD, wF - wB) / (2.0 * dx);

if (length(eta) > 0.001)
{
    float3 N = normalize(eta);
    float3 curl = CurlRead[id].xyz;

    float3 vorticityForce = VorticityStrength * cross(N, curl) * dx;
    vel += vorticityForce * DeltaTime;
}
```

## レイマーチングシェーダ
HDRP や UE5 と違って、URP はボリュームテクスチャの描画がないため、自分で作る必要です。
まあ、想像より簡単なものでしょうけどね。基本的にボリュームをスライスして、重ねて描画します。
問題はこれは木目の模様みたいにスライスの破片がすごく目立つため、何とか3次元でブレンドする必要です。

### 基本の処理
まず、フラグメントとカメラの位置を使って方向のベクトルを取得します。
その方向にマーチングします。

かくステップがさらに深くボリュームに張りますので、今までの色が新しいスライスの上に描画するイメージです。
その想定でブレンドしたら問題なく、簡単にボリュームを描けます。

```hlsl
float3 rayDir = normalize(IN.localPos - IN.localCamPos);
float3 rayPos = IN.localPos + float3(0.5, 0.5, 0.5);

float3 finalColor = float3(0.0, 0.0, 0.0);

float transparency = 1.0;
for (int step = 0; step < 32; step++)
{
    if (any(rayPos < 0) || any(rayPos > 1)) break;
    if (transparency < 0.01) break;

    float4 volumeData = SAMPLE_TEXTURE3D(_VolumeTex, sampler_VolumeTex, rayPos);

    // volumeData の熱と煙の量などの値を使ってこのステップの色を計算します
    float3 voxelColor = calculateColor(volumeData);
    float  voxelAlpha = calculateAlpha(volumeData);

    // transparency = 1 - alpha のでこれは乗算済みアルファで alpha:1-alpha のブレンド
    finalColor += voxelColor * voxelAlpha * transparency;
    transparency *= 1.0 - voxelAlpha

    rayPos += rayDir * _StepSize;
}

return float4(finalColor, 1.0 - transparency);
```

### 色の計算：黒体放射
あったかい物が光を放射します、これは黒体放射と言います、炎も同じです。
炎が複雑の物ですが、基本的に炭素がすごく厚くなって光らせています。

本当の計算が複雑すぎので [Tanner Helland の概算](https://tannerhelland.com/2012/09/18/convert-temperature-rgb-algorithm-code.html)を使います

```hlsl
float3 blackbodyColor(float heat)
{
    // Tanner Helland approximation for < 6600K
    float temp = lerp(_MinTemperature, _MaxTemperature, heat);
    float3 color;
    color.r = 1.0;
    color.g = saturate(0.3900815788 * log(temp) - 0.6318414438);
    color.b = (temp <= 19) ? 0.0 : saturate(0.5432067891 * log(temp - 10) - 1.1962540891);

    color = color * heat * heat * _EmissionIntensity;

    return color;
}
```

### ジッターについて
ここまではスライスごとで描画していますが、スライスとスライスの間をブレンドしてちゃんとした3次元のもので表示が必要です。
これも意外と簡単です、かくレイの位置をランダムにずらして計算する。
これだけだとノイズがすごくひどくなりますが、フレームごとに別の位置を選ぶので単なるの TAA でかなりなめらかに描画できます。

#### GPU の RNG
これがよくみる GPU の RNG 関数です
```
float random(float2 st)
{
    return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
}
```
とっても分かりにくいやつでしょうね。でもちゃんとした [0, 1) のランダム数になります。
大きい数字だと GPU の三角関数がかなり正確ではない、大体ランダムになります。

#### ジッターの実装
本当に rayDir にちょっとずらすだけです
```
float jitter = random(IN.positionCS.xy + _Time.y);
rayPos += rayDir * (jitter * _StepSize);
IN.worldPos += normalize(IN.worldPos - GetCameraPositionWS()) * (jitter + _StepSize);
```

## 最低限の流体力学

### Navier-Stokes

流体力学とは、流れる物の動きの研究です。
みんなが学校で勉強した物体の動きのニュートン力学と違って、集団で動く物に関する領域です。

ニュートン力学の場合は有名な式で物の動きを計算できます。

$$
\bold{F} = m \bold{a}
$$

ですが、力学で一番ほしい情報は速度の変化です。微積分で書き直して、

$$
\frac{d \bold{v}}{d t} = \frac{1}{m} \bold{F}
$$

になります。

流体力学にも同じ役割の式がもちろんあります。

$$
\frac{D \bold{u}}{D t} = \nu \nabla^2 \bold{u} - \frac{1}{\rho}\nabla p + \frac{1}{\rho}\bold{f}
$$

ニュートン力学に比べたらかなり複雑ですが、形は似ています。
左側には速度の変化で、右側にはそれを影響することがあります。

項を1個ずつ説明します。

* $\frac{D \bold{u}}{D t}$: これは速度の導関数。流体の場合は $\bold{v}$ ではなく $\bold{u}$ を使います。あと導関数は $d$ ではなく $D$ で書きます。これについては後ほど説明します。
* $\nu \nabla^2 \bold{u}$: 粘性による力です。流体自体が動きに抵抗する力ですね。気体の場合は $\nu$ がほとんど $0$ なので、今回はこれを無視して大丈夫です。
* $\frac{1}{\rho}\nabla p$: 圧力による力です。流体の一部が周りの流体を押す力ですね。非圧縮性流体でもちゃんと存在します。
* $\frac{1}{\rho}\bold{f}$: 外部からの力です。流体の場合は質量の $m$ ではなく、質量密度の $\rho$ を使いますが、ニュートンの運動方程式と同じ役割です。

ニュートン力学に比べると、流体内部からの力が追加されていますが、まあ似たような式です。

実はこれは非圧縮性のケースです。圧力が変わっても体積（密度）が変わらないということです。
気体でも意外とこれで大丈夫です。密閉された箱の中やマッハに近い速度の場合はさすがにダメですが、一般的なケースでは気にしなくていいですよ。
爆発を表現したいならさすがにこれではアウトですが、完全に物理学に則ったシミュレーションは必要ありません。
まず非圧縮性で計算をしてから、物理学的に厳密でなくても爆発っぽい見た目を追加する、といった感じで問題ないです。


### 移流（Dの意志）

導関数では $d$ ではなく $D$ を使いましたが、これにはちゃんと意味があります。
これは物質微分（実質微分）というものです。

流体において速度を変化させるのは力だけでなく、流れていること自体によって粒子が動いていることも関係します。
欲しいのは特定の粒子の速度ではなく、「特定の場所」にある粒子の速度です。
粒子が速度（や温度などの他のパラメータ）を運んで別の場所に移動することを「移流」と言います。

物質微分は、ある粒子（流体要素）に沿った変化という意味です。
シミュレーションのためには、特定の場所での速度の変化が知りたいです。
それはこちらの式で計算できます。

$$
\frac{D \bold{u}}{D t} = \frac{\partial \bold{u}}{\partial t} + (\bold{u} \cdot \nabla)\bold{u}
$$

また、項ごとに説明します。

* $\frac{\partial \bold{u}}{\partial t}$: 特定の場所での速度の変化。計算したいのはこれです。
* $(\bold{u} \cdot \nabla)\bold{u}$: すごい書き方ですが、これは速度の移流です。流体の難しさは大体ここに詰まっています。

移流はまあ、計算というよりシミュレーションで処理するので深く気にしなくても大丈夫ですが、調べたい人はこの数文字の部分だけでも奥がかなり深いことがわかると思います。

### 発散と回転

ベクトル場の微分にはいくつか種類があります。
発散と回転の計算が必要になりますので、短めに説明します。

#### 発散

$$
\nabla \cdot \bold{F}
$$

ベクトルがある場所から湧き出していく（正の発散）か、集まっていく（負の発散）かを表します。
発散がちょうど0の場合は、単純にそのまま流れているイメージです。
特に今回は非圧縮性のシミュレーションなので、発散は0になるはずです。

#### 回転

$$
\nabla \times \bold{F}
$$

ベクトルがどれくらい渦を巻いているかの計算です。結果もベクトルになります。
物理学の授業で学んだ（はず）の右手の法則で、渦を巻く方向がわかります。
この渦の動きがすごく流体っぽいので、ちゃんと表現したいですね。

### 圧力の計算

非圧縮性の場合は、発散をゼロに保つ役割を果たすのが圧力です。
流れがある場所に集中しそうになったら、その場所の圧力が上がって、発散がゼロの状態を保つような感じです。
この条件を守るための圧力はポアソン方程式で計算します。

$$
\nabla^2 p = \nabla \cdot \bold{u}
$$

これは一発で計算できないやつなので、何回も反復して正しい圧力に近づけていくしかないです。
FPSが死ぬ原因は大体ここです。。。

### まとめ

流体シミュレーションのために空間をボクセルに分けて、各場所での $\frac{\partial \bold{u}}{\partial t}$ を計算します。
これを計算するには Navier-Stokes 方程式を以下のように利用します。

$$
\frac{\partial \bold{u}}{\partial t} = - (\bold{u} \cdot \nabla)\bold{u} - \frac{1}{\rho}\nabla p + \frac{1}{\rho}\bold{f}
$$

言葉にするとこれは、

```text
[速度の変化] = -[移流] - [圧力による力] + [外部からの力]
```

圧力のほうがポアソン方程式で計算します
$$
\nabla^2 p = \nabla \cdot \bold{u}
$$
