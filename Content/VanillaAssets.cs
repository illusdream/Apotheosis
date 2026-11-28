#nullable enable

using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using ReLogic.Graphics;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;

namespace Apotheosis
{
    /// <summary>
    /// 常用原版泰拉瑞亚（Vanilla Terraria）高频特殊资源的语义化快捷入口。
    /// <para>
    /// 包含：高频 VFX / 噪声 / 拖尾 / <c>Extra_*</c> 贴图、经 <c>tModLoader.dll</c> 与 Wiki 核对的语义化 <see cref="SoundID"/> 音效、以及原版字体。
    /// 所有贴图与字体均采用与 <see cref="Assets"/> 一致的 <c>const string XxxPath</c> + <c>??=</c> 懒加载模式，并在模组卸载时自动清空缓存。
    /// </para>
    /// </summary>
    public static partial class VanillaAssets
    {
        /// <summary>
        /// 高频原版特效、噪声、拖尾、光晕与基础像素贴图（全部已核对原版 <c>Content/Images</c> 与 <see cref="ExtrasID"/>）。
        /// </summary>
        public static partial class Vfx
        {
            private static readonly Dictionary<short, Asset<Texture2D>> _extraCache = new();

            // ==================== 基础 / 噪声 / 光晕 ====================

            /// <summary>1x1 纯白像素（画线、矩形、纯色填充、Shader 底图必备）。</summary>
            public const string MagicPixelPath = "Terraria/Images/MagicPixel";
            private static Asset<Texture2D>? _magicPixel;
            public static Asset<Texture2D> MagicPixel => _magicPixel ??= ModContent.Request<Texture2D>(MagicPixelPath);

            /// <summary>原版火焰圆环贴图（<c>Terraria/Images/FlameRing</c>）。</summary>
            public const string FlameRingPath = "Terraria/Images/FlameRing";
            private static Asset<Texture2D>? _flameRing;
            public static Asset<Texture2D> FlameRing => _flameRing ??= ModContent.Request<Texture2D>(FlameRingPath);

            /// <summary>柏林噪声贴图（<c>Terraria/Images/Misc/Perlin</c>，常用于 Shader 扭曲、能量流动、溶解）。</summary>
            public const string PerlinNoisePath = "Terraria/Images/Misc/Perlin";
            private static Asset<Texture2D>? _perlinNoise;
            public static Asset<Texture2D> PerlinNoise => _perlinNoise ??= ModContent.Request<Texture2D>(PerlinNoisePath);

            /// <summary>通用噪声贴图（<c>Terraria/Images/Misc/noise</c>）。</summary>
            public const string NoisePath = "Terraria/Images/Misc/noise";
            private static Asset<Texture2D>? _noise;
            public static Asset<Texture2D> Noise => _noise ??= ModContent.Request<Texture2D>(NoisePath);

            /// <summary>水波纹扰动贴图（<c>Terraria/Images/Misc/Ripples</c>）。</summary>
            public const string RipplesPath = "Terraria/Images/Misc/Ripples";
            private static Asset<Texture2D>? _ripples;
            public static Asset<Texture2D> Ripples => _ripples ??= ModContent.Request<Texture2D>(RipplesPath);

            /// <summary>太阳镜头光晕 1（<c>Terraria/Images/Misc/Sunflare/Lens/Flare1</c>）。</summary>
            public const string LensFlare1Path = "Terraria/Images/Misc/Sunflare/Lens/Flare1";
            private static Asset<Texture2D>? _lensFlare1;
            public static Asset<Texture2D> LensFlare1 => _lensFlare1 ??= ModContent.Request<Texture2D>(LensFlare1Path);

            /// <summary>太阳镜头光晕 2（<c>Terraria/Images/Misc/Sunflare/Lens/Flare2</c>）。</summary>
            public const string LensFlare2Path = "Terraria/Images/Misc/Sunflare/Lens/Flare2";
            private static Asset<Texture2D>? _lensFlare2;
            public static Asset<Texture2D> LensFlare2 => _lensFlare2 ??= ModContent.Request<Texture2D>(LensFlare2Path);

            /// <summary>柔和圆形光点（<c>Terraria/Images/Misc/Sunflare/Lens/PointBlurry</c>）。</summary>
            public const string PointBlurryPath = "Terraria/Images/Misc/Sunflare/Lens/PointBlurry";
            private static Asset<Texture2D>? _pointBlurry;
            public static Asset<Texture2D> PointBlurry => _pointBlurry ??= ModContent.Request<Texture2D>(PointBlurryPath);

            /// <summary>锐利圆形光点（<c>Terraria/Images/Misc/Sunflare/Lens/PointSharp</c>）。</summary>
            public const string PointSharpPath = "Terraria/Images/Misc/Sunflare/Lens/PointSharp";
            private static Asset<Texture2D>? _pointSharp;
            public static Asset<Texture2D> PointSharp => _pointSharp ??= ModContent.Request<Texture2D>(PointSharpPath);

            /// <summary>星云天空光束（<c>Terraria/Images/Misc/NebulaSky/Beam</c>）。</summary>
            public const string NebulaBeamPath = "Terraria/Images/Misc/NebulaSky/Beam";
            private static Asset<Texture2D>? _nebulaBeam;
            public static Asset<Texture2D> NebulaBeam => _nebulaBeam ??= ModContent.Request<Texture2D>(NebulaBeamPath);

            /// <summary>星旋天空闪电（<c>Terraria/Images/Misc/VortexSky/Bolt</c>）。</summary>
            public const string VortexBoltPath = "Terraria/Images/Misc/VortexSky/Bolt";
            private static Asset<Texture2D>? _vortexBolt;
            public static Asset<Texture2D> VortexBolt => _vortexBolt ??= ModContent.Request<Texture2D>(VortexBoltPath);

            /// <summary>星旋天空闪光（<c>Terraria/Images/Misc/VortexSky/Flash</c>）。</summary>
            public const string VortexFlashPath = "Terraria/Images/Misc/VortexSky/Flash";
            private static Asset<Texture2D>? _vortexFlash;
            public static Asset<Texture2D> VortexFlash => _vortexFlash ??= ModContent.Request<Texture2D>(VortexFlashPath);

            // ==================== 高频 Extra_* 特效 / 拖尾 / 溶解 ====================

            /// <summary><c>Extra_33</c>（<see cref="ExtrasID.CultistLightingArc"/>）：拜月教邪教徒闪电弧。</summary>
            public const string CultistLightningArcPath = "Terraria/Images/Extra_33";
            private static Asset<Texture2D>? _cultistLightningArc;
            public static Asset<Texture2D> CultistLightningArc => _cultistLightningArc ??= ModContent.Request<Texture2D>(CultistLightningArcPath);

            /// <summary><c>Extra_34</c>（<see cref="ExtrasID.CultistRitual"/>）：拜月教邪教徒仪式法阵。</summary>
            public const string CultistRitualPath = "Terraria/Images/Extra_34";
            private static Asset<Texture2D>? _cultistRitual;
            public static Asset<Texture2D> CultistRitual => _cultistRitual ??= ModContent.Request<Texture2D>(CultistRitualPath);

            /// <summary><c>Extra_50</c>（<see cref="ExtrasID.VortexBlack"/>）：星旋暗核/黑洞球体。</summary>
            public const string VortexBlackPath = "Terraria/Images/Extra_50";
            private static Asset<Texture2D>? _vortexBlack;
            public static Asset<Texture2D> VortexBlack => _vortexBlack ??= ModContent.Request<Texture2D>(VortexBlackPath);

            /// <summary><c>Extra_51</c>（<see cref="ExtrasID.MagicAura"/>）：魔法光环。</summary>
            public const string MagicAuraPath = "Terraria/Images/Extra_51";
            private static Asset<Texture2D>? _magicAura;
            public static Asset<Texture2D> MagicAura => _magicAura ??= ModContent.Request<Texture2D>(MagicAuraPath);

            /// <summary><c>Extra_57</c>（<see cref="ExtrasID.AncientLight"/>）：远古光球。</summary>
            public const string AncientLightPath = "Terraria/Images/Extra_57";
            private static Asset<Texture2D>? _ancientLight;
            public static Asset<Texture2D> AncientLight => _ancientLight ??= ModContent.Request<Texture2D>(AncientLightPath);

            /// <summary><c>Extra_58</c>（<see cref="ExtrasID.DefenseShield"/>）：防御护盾罩。</summary>
            public const string DefenseShieldPath = "Terraria/Images/Extra_58";
            private static Asset<Texture2D>? _defenseShield;
            public static Asset<Texture2D> DefenseShield => _defenseShield ??= ModContent.Request<Texture2D>(DefenseShieldPath);

            /// <summary><c>Extra_59</c>（<see cref="ExtrasID.PortalGateHalo"/>）：传送门光环。</summary>
            public const string PortalGateHaloPath = "Terraria/Images/Extra_59";
            private static Asset<Texture2D>? _portalGateHalo;
            public static Asset<Texture2D> PortalGateHalo => _portalGateHalo ??= ModContent.Request<Texture2D>(PortalGateHaloPath);

            /// <summary><c>Extra_89</c>（<see cref="ExtrasID.ThePerfectGlow"/>）：柔和中心发光球。</summary>
            public const string ThePerfectGlowPath = "Terraria/Images/Extra_89";
            private static Asset<Texture2D>? _thePerfectGlow;
            public static Asset<Texture2D> ThePerfectGlow => _thePerfectGlow ??= ModContent.Request<Texture2D>(ThePerfectGlowPath);

            /// <summary><c>Extra_91</c>（<see cref="ExtrasID.FallingStar"/>）：坠星光柱/拖尾。</summary>
            public const string FallingStarPath = "Terraria/Images/Extra_91";
            private static Asset<Texture2D>? _fallingStar;
            public static Asset<Texture2D> FallingStar => _fallingStar ??= ModContent.Request<Texture2D>(FallingStarPath);

            /// <summary><c>Extra_98</c>（<see cref="ExtrasID.SharpTears"/>）：经典锐利十字菱形星芒（高光/星芒/刀光常用）。</summary>
            public const string Extra98Path = "Terraria/Images/Extra_98";
            private static Asset<Texture2D>? _extra98;
            public static Asset<Texture2D> Extra98 => _extra98 ??= ModContent.Request<Texture2D>(Extra98Path);

            /// <summary><c>Extra_174</c>（<see cref="ExtrasID.KeybrandRing"/>）：圆环冲击波。</summary>
            public const string KeybrandRingPath = "Terraria/Images/Extra_174";
            private static Asset<Texture2D>? _keybrandRing;
            public static Asset<Texture2D> KeybrandRing => _keybrandRing ??= ModContent.Request<Texture2D>(KeybrandRingPath);

            /// <summary><c>Extra_179</c>（<see cref="ExtrasID.Voronoi"/>）：沃罗诺伊噪声（Shader 溶解/能量纹理常用）。</summary>
            public const string VoronoiNoisePath = "Terraria/Images/Extra_179";
            private static Asset<Texture2D>? _voronoiNoise;
            public static Asset<Texture2D> VoronoiNoise => _voronoiNoise ??= ModContent.Request<Texture2D>(VoronoiNoisePath);

            /// <summary><c>Extra_189</c>（<see cref="ExtrasID.FlameLashTrailShape"/>）：烈焰火鞭拖尾形状。</summary>
            public const string FlameLashTrailShapePath = "Terraria/Images/Extra_189";
            private static Asset<Texture2D>? _flameLashTrailShape;
            public static Asset<Texture2D> FlameLashTrailShape => _flameLashTrailShape ??= ModContent.Request<Texture2D>(FlameLashTrailShapePath);

            /// <summary><c>Extra_190</c>（<see cref="ExtrasID.FlameLashTrailErosion"/>）：烈焰火鞭拖尾溶解贴图。</summary>
            public const string FlameLashTrailErosionPath = "Terraria/Images/Extra_190";
            private static Asset<Texture2D>? _flameLashTrailErosion;
            public static Asset<Texture2D> FlameLashTrailErosion => _flameLashTrailErosion ??= ModContent.Request<Texture2D>(FlameLashTrailErosionPath);

            /// <summary><c>Extra_191</c>（<see cref="ExtrasID.FlameLashTrailColorGradient"/>）：烈焰火鞭拖尾色彩渐变。</summary>
            public const string FlameLashTrailGradientPath = "Terraria/Images/Extra_191";
            private static Asset<Texture2D>? _flameLashTrailGradient;
            public static Asset<Texture2D> FlameLashTrailGradient => _flameLashTrailGradient ??= ModContent.Request<Texture2D>(FlameLashTrailGradientPath);

            /// <summary><c>Extra_192</c>（<see cref="ExtrasID.MagicMissileTrailColorGradient"/>）：魔法飞弹拖尾色彩渐变。</summary>
            public const string MagicMissileTrailGradientPath = "Terraria/Images/Extra_192";
            private static Asset<Texture2D>? _magicMissileTrailGradient;
            public static Asset<Texture2D> MagicMissileTrailGradient => _magicMissileTrailGradient ??= ModContent.Request<Texture2D>(MagicMissileTrailGradientPath);

            /// <summary><c>Extra_193</c>（<see cref="ExtrasID.MagicMissileTrailErosion"/>）：魔法飞弹拖尾溶解/柔光云雾。</summary>
            public const string MagicMissileTrailErosionPath = "Terraria/Images/Extra_193";
            private static Asset<Texture2D>? _magicMissileTrailErosion;
            public static Asset<Texture2D> MagicMissileTrailErosion => _magicMissileTrailErosion ??= ModContent.Request<Texture2D>(MagicMissileTrailErosionPath);

            /// <summary><c>Extra_194</c>（<see cref="ExtrasID.MagicMissileTrailShape"/>）：魔法飞弹拖尾形状。</summary>
            public const string MagicMissileTrailShapePath = "Terraria/Images/Extra_194";
            private static Asset<Texture2D>? _magicMissileTrailShape;
            public static Asset<Texture2D> MagicMissileTrailShape => _magicMissileTrailShape ??= ModContent.Request<Texture2D>(MagicMissileTrailShapePath);

            /// <summary><c>Extra_195</c>（<see cref="ExtrasID.RainbowRodTrailColorGradient"/>）：彩虹魔杖拖尾色彩渐变。</summary>
            public const string RainbowRodTrailGradientPath = "Terraria/Images/Extra_195";
            private static Asset<Texture2D>? _rainbowRodTrailGradient;
            public static Asset<Texture2D> RainbowRodTrailGradient => _rainbowRodTrailGradient ??= ModContent.Request<Texture2D>(RainbowRodTrailGradientPath);

            /// <summary><c>Extra_196</c>（<see cref="ExtrasID.RainbowRodTrailErosion"/>）：彩虹魔杖拖尾溶解贴图。</summary>
            public const string RainbowRodTrailErosionPath = "Terraria/Images/Extra_196";
            private static Asset<Texture2D>? _rainbowRodTrailErosion;
            public static Asset<Texture2D> RainbowRodTrailErosion => _rainbowRodTrailErosion ??= ModContent.Request<Texture2D>(RainbowRodTrailErosionPath);

            /// <summary><c>Extra_197</c>（<see cref="ExtrasID.RainbowRodTrailShape"/>）：彩虹魔杖拖尾形状。</summary>
            public const string RainbowRodTrailShapePath = "Terraria/Images/Extra_197";
            private static Asset<Texture2D>? _rainbowRodTrailShape;
            public static Asset<Texture2D> RainbowRodTrailShape => _rainbowRodTrailShape ??= ModContent.Request<Texture2D>(RainbowRodTrailShapePath);

            /// <summary><c>Extra_201</c>（<see cref="ExtrasID.FinalFractal"/>）：天顶剑斩击拖尾。</summary>
            public const string FinalFractalTrailPath = "Terraria/Images/Extra_201";
            private static Asset<Texture2D>? _finalFractalTrail;
            public static Asset<Texture2D> FinalFractalTrail => _finalFractalTrail ??= ModContent.Request<Texture2D>(FinalFractalTrailPath);

            /// <summary><c>Extra_209</c>（<see cref="ExtrasID.EmpressBladeTrail"/>）：光之女皇泰拉棱镜剑光拖尾。</summary>
            public const string EmpressBladeTrailPath = "Terraria/Images/Extra_209";
            private static Asset<Texture2D>? _empressBladeTrail;
            public static Asset<Texture2D> EmpressBladeTrail => _empressBladeTrail ??= ModContent.Request<Texture2D>(EmpressBladeTrailPath);

            /// <summary><c>Extra_210</c>（<see cref="ExtrasID.EmpressBladeErosion"/>）：光之女皇剑光溶解贴图。</summary>
            public const string EmpressBladeErosionPath = "Terraria/Images/Extra_210";
            private static Asset<Texture2D>? _empressBladeErosion;
            public static Asset<Texture2D> EmpressBladeErosion => _empressBladeErosion ??= ModContent.Request<Texture2D>(EmpressBladeErosionPath);

            /// <summary><c>Extra_267</c>（<see cref="ExtrasID.GenericCircle"/>）：通用圆形贴图。</summary>
            public const string GenericCirclePath = "Terraria/Images/Extra_267";
            private static Asset<Texture2D>? _genericCircle;
            public static Asset<Texture2D> GenericCircle => _genericCircle ??= ModContent.Request<Texture2D>(GenericCirclePath);

            /// <summary><c>Extra_286</c>（<see cref="ExtrasID.AuroraNoise"/>）：极光噪声贴图。</summary>
            public const string AuroraNoisePath = "Terraria/Images/Extra_286";
            private static Asset<Texture2D>? _auroraNoise;
            public static Asset<Texture2D> AuroraNoise => _auroraNoise ??= ModContent.Request<Texture2D>(AuroraNoisePath);

            /// <summary><c>Extra_287</c>（<see cref="ExtrasID.AuroraShape"/>）：极光形状贴图。</summary>
            public const string AuroraShapePath = "Terraria/Images/Extra_287";
            private static Asset<Texture2D>? _auroraShape;
            public static Asset<Texture2D> AuroraShape => _auroraShape ??= ModContent.Request<Texture2D>(AuroraShapePath);

            /// <summary><c>Extra_288</c>（<see cref="ExtrasID.LightningShape"/>）：闪电形状贴图。</summary>
            public const string LightningShapePath = "Terraria/Images/Extra_288";
            private static Asset<Texture2D>? _lightningShape;
            public static Asset<Texture2D> LightningShape => _lightningShape ??= ModContent.Request<Texture2D>(LightningShapePath);

            // ==================== 按 ExtrasID 兜底懒加载 ====================

            /// <summary>获取指定 <see cref="ExtrasID"/> 对应的 tML 资源路径。</summary>
            public static string ExtraPath(short extraId) => $"Terraria/Images/Extra_{extraId}";

            /// <summary>按 <see cref="ExtrasID"/> 懒加载并缓存任意原版 <c>Extra_*</c> 贴图。</summary>
            public static Asset<Texture2D> Extra(short extraId)
            {
                if (!_extraCache.TryGetValue(extraId, out var asset))
                {
                    asset = ModContent.Request<Texture2D>(ExtraPath(extraId));
                    _extraCache[extraId] = asset;
                }

                return asset;
            }

            [EditorBrowsable(EditorBrowsableState.Never)]
            internal static void __Unload()
            {
                _extraCache.Clear();
                _magicPixel = null;
                _flameRing = null;
                _perlinNoise = null;
                _noise = null;
                _ripples = null;
                _lensFlare1 = null;
                _lensFlare2 = null;
                _pointBlurry = null;
                _pointSharp = null;
                _nebulaBeam = null;
                _vortexBolt = null;
                _vortexFlash = null;
                _cultistLightningArc = null;
                _cultistRitual = null;
                _vortexBlack = null;
                _magicAura = null;
                _ancientLight = null;
                _defenseShield = null;
                _portalGateHalo = null;
                _thePerfectGlow = null;
                _fallingStar = null;
                _extra98 = null;
                _keybrandRing = null;
                _voronoiNoise = null;
                _flameLashTrailShape = null;
                _flameLashTrailErosion = null;
                _flameLashTrailGradient = null;
                _magicMissileTrailGradient = null;
                _magicMissileTrailErosion = null;
                _magicMissileTrailShape = null;
                _rainbowRodTrailGradient = null;
                _rainbowRodTrailErosion = null;
                _rainbowRodTrailShape = null;
                _finalFractalTrail = null;
                _empressBladeTrail = null;
                _empressBladeErosion = null;
                _genericCircle = null;
                _auroraNoise = null;
                _auroraShape = null;
                _lightningShape = null;
            }
        }

        /// <summary>
        /// 按语义分类的常用原版音效。
        /// <para>
        /// 直接返回对应的 <see cref="SoundID"/> 字段以完整保留原版音量、音高随机范围与多变体配置；
        /// 全部条目均已通过本机 <c>tModLoader.dll</c> 物品/弹幕/NPC 实际引用与官方 Wiki 交叉核对。
        /// </para>
        /// </summary>
        public static partial class Sounds
        {
            /// <summary>近战、挥砍、剑气与鞭类音效。</summary>
            public static partial class Melee
            {
                /// <summary><see cref="SoundID.Item1"/>：通用挥砍声（原版代表：铁阔剑、木剑、各类镐斧工具）。</summary>
                public static SoundStyle Swing => SoundID.Item1;

                /// <summary><see cref="SoundID.Item15"/>：光剑挥舞嗡鸣声（原版代表：各色陨石光剑 / 晶光刃）。</summary>
                public static SoundStyle Phaseblade => SoundID.Item15;

                /// <summary><see cref="SoundID.Item60"/>：剑气/能量斩波声（原版代表：泰拉刃、永夜刃、魔光剑斩击弹幕）。</summary>
                public static SoundStyle SwordBeam => SoundID.Item60;

                /// <summary><see cref="SoundID.Item71"/>：重型镰刀破空斩击声（原版代表：死神镰刀）。</summary>
                public static SoundStyle HeavySlash => SoundID.Item71;

                /// <summary><see cref="SoundID.Item105"/>：星辰重斩声（原版代表：狂星之怒）。</summary>
                public static SoundStyle StarWrath => SoundID.Item105;

                /// <summary><see cref="SoundID.Item152"/>：鞭子挥动声（原版代表：<c>Item.DefaultToWhip</c>）。</summary>
                public static SoundStyle WhipSwing => SoundID.Item152;

                /// <summary><see cref="SoundID.Item153"/>：鞭子抽打爆响声（原版代表：<c>Projectile.AI_165_Whip</c>）。</summary>
                public static SoundStyle WhipCrack => SoundID.Item153;

                /// <summary><see cref="SoundID.Item169"/>：天顶剑高速斩击声（原版代表：天顶剑 <c>AI_182_FinalFractal</c>）。</summary>
                public static SoundStyle Zenith => SoundID.Item169;

                /// <summary><see cref="SoundID.DD2_MonkStaffSwing"/>：长棍/长柄挥舞声（原版代表：瞌睡章鱼/武僧长杖）。</summary>
                public static SoundStyle StaffSwing => SoundID.DD2_MonkStaffSwing;

                /// <summary><see cref="SoundID.DD2_SonicBoomBladeSlash"/>：音爆剑气斩声（原版代表：飞龙）。</summary>
                public static SoundStyle SonicBoomSlash => SoundID.DD2_SonicBoomBladeSlash;
            }

            /// <summary>弓箭、枪械、喷射器与远程投掷音效。</summary>
            public static partial class Ranged
            {
                /// <summary><see cref="SoundID.Item5"/>：弓箭/连弩发射声（原版代表：木弓、恶魔弓、秘银/精金/神圣连弩）。</summary>
                public static SoundStyle Bow => SoundID.Item5;

                /// <summary><see cref="SoundID.Item11"/>：通用枪械/发射器射击声（原版代表：迷你鲨、巨兽鲨、火箭发射器）。</summary>
                public static SoundStyle Gun => SoundID.Item11;

                /// <summary><see cref="SoundID.Item31"/>：三连发突击步枪声（原版代表：发条式突击步枪）。</summary>
                public static SoundStyle BurstRifle => SoundID.Item31;

                /// <summary><see cref="SoundID.Item34"/>：火焰/气流喷射声（原版代表：火焰喷射器、环境改造枪、精灵熔炉）。</summary>
                public static SoundStyle Flamethrower => SoundID.Item34;

                /// <summary><see cref="SoundID.Item36"/>：重型霰弹轰鸣声（原版代表：霰弹枪、三发猎枪、玛瑙爆破枪）。</summary>
                public static SoundStyle Shotgun => SoundID.Item36;

                /// <summary><see cref="SoundID.Item38"/>：战术霰弹枪沉重轰鸣（原版代表：战术霰弹枪）。</summary>
                public static SoundStyle TacticalShotgun => SoundID.Item38;

                /// <summary><see cref="SoundID.Item39"/>：快速连射/投掷声（原版代表：吸血鬼刀、腐化者之戟、剃刀松）。</summary>
                public static SoundStyle RapidThrow => SoundID.Item39;

                /// <summary><see cref="SoundID.Item40"/>：高能狙击枪声（原版代表：狙击步枪、太空海豚机枪）。</summary>
                public static SoundStyle Sniper => SoundID.Item40;

                /// <summary><see cref="SoundID.Item41"/>：手枪/高射速机枪声（原版代表：手枪、凤凰爆破枪、链式机枪）。</summary>
                public static SoundStyle Handgun => SoundID.Item41;

                /// <summary><see cref="SoundID.Item61"/>：榴弹发射器闷响（原版代表：榴弹发射器）。</summary>
                public static SoundStyle GrenadeLauncher => SoundID.Item61;

                /// <summary><see cref="SoundID.Item75"/>：能量弓/蓄力炮射击声（原版代表：脉冲弓、充能爆破炮）。</summary>
                public static SoundStyle PulseBow => SoundID.Item75;

                /// <summary><see cref="SoundID.Item98"/>：飞镖手枪射击声（原版代表：飞镖手枪）。</summary>
                public static SoundStyle DartPistol => SoundID.Item98;

                /// <summary><see cref="SoundID.Item99"/>：飞镖步枪射击声（原版代表：飞镖步枪）。</summary>
                public static SoundStyle DartRifle => SoundID.Item99;

                /// <summary><see cref="SoundID.Item102"/>：暗影焰弓射击声（原版代表：暗影焰弓、空中祸害）。</summary>
                public static SoundStyle ShadowFlameBow => SoundID.Item102;
            }

            /// <summary>法术施放、激光、元素魔法与召唤音效。</summary>
            public static partial class Magic
            {
                /// <summary><see cref="SoundID.Item8"/>：经典施法/传送音效（原版代表：恶魔之镰、混乱传送杖、火花魔棒）。</summary>
                public static SoundStyle Cast => SoundID.Item8;

                /// <summary><see cref="SoundID.Item9"/>：星辰/魔弹连发声（原版代表：魔法飞弹、星星炮、水晶风暴、裂天剑）。</summary>
                public static SoundStyle StarCast => SoundID.Item9;

                /// <summary><see cref="SoundID.Item12"/>：经典激光发射声（原版代表：激光步枪、高温射线枪）。</summary>
                public static SoundStyle Laser => SoundID.Item12;

                /// <summary><see cref="SoundID.Item13"/>：水流喷射声（原版代表：海蓝权杖、黄金雨）。</summary>
                public static SoundStyle WaterStream => SoundID.Item13;

                /// <summary><see cref="SoundID.Item20"/>：火焰/冰霜/星云元素施法声（原版代表：火之花、烈焰火鞭、咒火、冰霜法杖、星云烈焰）。</summary>
                public static SoundStyle ElementalCast => SoundID.Item20;

                /// <summary><see cref="SoundID.Item21"/>：水矢发射声（原版代表：水矢）。</summary>
                public static SoundStyle WaterBolt => SoundID.Item21;

                /// <summary><see cref="SoundID.Item28"/>：彩虹/冰晶魔杖施法声（原版代表：彩虹魔杖、冰雪魔杖）。</summary>
                public static SoundStyle CrystalCast => SoundID.Item28;

                /// <summary><see cref="SoundID.Item43"/>：宝石法杖/幽灵法杖施法声（原版代表：各色宝石法杖、毒液法杖、幽灵法杖）。</summary>
                public static SoundStyle GemStaff => SoundID.Item43;

                /// <summary><see cref="SoundID.Item44"/>：召唤仆从施法声（原版代表：史莱姆法杖、乌鸦法杖、星尘细胞/星尘之龙法杖）。</summary>
                public static SoundStyle Summon => SoundID.Item44;

                /// <summary><see cref="SoundID.Item67"/>：彩虹光束发射声（原版代表：彩虹枪）。</summary>
                public static SoundStyle RainbowGun => SoundID.Item67;

                /// <summary><see cref="SoundID.Item72"/>：瞬发暗影激光声（原版代表：暗影束法杖）。</summary>
                public static SoundStyle Shadowbeam => SoundID.Item72;

                /// <summary><see cref="SoundID.Item73"/>：狱火法球发射声（原版代表：狱火叉）。</summary>
                public static SoundStyle InfernoCast => SoundID.Item73;

                /// <summary><see cref="SoundID.Item84"/>：旋风/水刃召唤声（原版代表：利刃台风）。</summary>
                public static SoundStyle Typhoon => SoundID.Item84;

                /// <summary><see cref="SoundID.Item88"/>：陨石/月耀降落施法声（原版代表：流星法杖、月耀）。</summary>
                public static SoundStyle MeteorCast => SoundID.Item88;

                /// <summary><see cref="SoundID.Item92"/>：电圈发射声（原版代表：电圈发射器）。</summary>
                public static SoundStyle Electrosphere => SoundID.Item92;

                /// <summary><see cref="SoundID.Item103"/>：暗影焰妖娃施法声（原版代表：暗影焰妖娃）。</summary>
                public static SoundStyle ShadowFlameHex => SoundID.Item103;

                /// <summary><see cref="SoundID.Item109"/>：水晶蛇发射声（原版代表：水晶蛇）。</summary>
                public static SoundStyle CrystalSerpent => SoundID.Item109;

                /// <summary><see cref="SoundID.Item117"/>：星云奥秘/幽灵烈焰施法声（原版代表：星云奥秘、神灯烈焰）。</summary>
                public static SoundStyle NebulaArcanum => SoundID.Item117;

                /// <summary><see cref="SoundID.Item157"/>：太空枪激光声（原版代表：太空枪）。</summary>
                public static SoundStyle SpaceGun => SoundID.Item157;

                /// <summary><see cref="SoundID.Item158"/>：冲击枪电流射击声（原版代表：灰/橙冲击枪）。</summary>
                public static SoundStyle Zapinator => SoundID.Item158;

                /// <summary><see cref="SoundID.Item176"/>：微光转化音效（原版代表：<c>WorldItem.ShimmerEffect</c>）。</summary>
                public static SoundStyle ShimmerTransform => SoundID.Item176;
            }

            /// <summary>爆炸、爆裂与雷电音效。</summary>
            public static partial class Explosion
            {
                /// <summary><see cref="SoundID.Item14"/>：标准爆炸声（原版代表：炸弹、手雷、火箭、弹幕爆破、石巨人拳头等）。</summary>
                public static SoundStyle Standard => SoundID.Item14;

                /// <summary><see cref="SoundID.Item62"/>：重型爆破声（原版代表：<c>Projectile.Kill_Bombs_DoUsualKillCode</c> 榴弹/炸弹爆破）。</summary>
                public static SoundStyle Heavy => SoundID.Item62;

                /// <summary><see cref="SoundID.Item74"/>：狱火爆裂声（原版代表：狱火叉火球爆裂）。</summary>
                public static SoundStyle Inferno => SoundID.Item74;

                /// <summary><see cref="SoundID.Thunder"/>：雷鸣声。</summary>
                public static SoundStyle Thunder => SoundID.Thunder;

                /// <summary><see cref="SoundID.InstantThunder"/>：瞬发雷击轰鸣声。</summary>
                public static SoundStyle InstantThunder => SoundID.InstantThunder;

                /// <summary><see cref="SoundID.LightningStrikeHit"/>：落雷击中声。</summary>
                public static SoundStyle LightningHit => SoundID.LightningStrikeHit;
            }

            /// <summary>Boss 吼叫与代表性 Boss 战斗音效。</summary>
            public static partial class Boss
            {
                /// <summary><see cref="SoundID.Roar"/>：经典 Boss 吼叫（克苏鲁之眼、世界吞噬怪、骷髅王等）。</summary>
                public static SoundStyle Roar => SoundID.Roar;

                /// <summary><see cref="SoundID.ForceRoar"/>：强制优先播放的 Boss 吼叫。</summary>
                public static SoundStyle ForceRoar => SoundID.ForceRoar;

                /// <summary><see cref="SoundID.MoonLord"/>：月球领主特征音效。</summary>
                public static SoundStyle MoonLord => SoundID.MoonLord;

                /// <summary><see cref="SoundID.QueenSlime"/>：史莱姆皇后特征音效。</summary>
                public static SoundStyle QueenSlime => SoundID.QueenSlime;

                /// <summary><see cref="SoundID.Item160"/>：光之女皇攻击音效 1（原版代表：<c>NPC.AI_120_HallowBoss</c>）。</summary>
                public static SoundStyle EmpressAttack1 => SoundID.Item160;

                /// <summary><see cref="SoundID.Item162"/>：光之女皇攻击音效 2（原版代表：<c>NPC.AI_120_HallowBoss</c>）。</summary>
                public static SoundStyle EmpressAttack2 => SoundID.Item162;

                /// <summary><see cref="SoundID.Item163"/>：光之女皇光束/爆发音效（原版代表：<c>NPC.AI_120_HallowBoss</c>）。</summary>
                public static SoundStyle EmpressBurst => SoundID.Item163;

                /// <summary><see cref="SoundID.ScaryScream"/>：尖啸声。</summary>
                public static SoundStyle ScaryScream => SoundID.ScaryScream;
            }

            /// <summary>消耗品、物理碰撞、移动与 UI 交互音效。</summary>
            public static partial class Misc
            {
                /// <summary><see cref="SoundID.Item2"/>：进食/咀嚼声（原版代表：蘑菇、食物及部分宠物召唤物）。</summary>
                public static SoundStyle Eat => SoundID.Item2;

                /// <summary><see cref="SoundID.Item3"/>：饮用药水声（原版代表：治疗药水、魔力药水、各类增益药水）。</summary>
                public static SoundStyle Drink => SoundID.Item3;

                /// <summary><see cref="SoundID.Item4"/>：强化/能量提升声（原版代表：生命水晶、生命果、坠星、恶魔之心）。</summary>
                public static SoundStyle PowerUp => SoundID.Item4;

                /// <summary><see cref="SoundID.Item6"/>：回城/传送声（原版代表：魔镜、回忆药水、魔法海螺、手机）。</summary>
                public static SoundStyle Teleport => SoundID.Item6;

                /// <summary><see cref="SoundID.Item29"/>：魔力水晶使用声（原版代表：魔力水晶）。</summary>
                public static SoundStyle ManaCrystal => SoundID.Item29;

                /// <summary><see cref="SoundID.MaxMana"/>：魔力回满提示音。</summary>
                public static SoundStyle MaxMana => SoundID.MaxMana;

                /// <summary><see cref="SoundID.Shatter"/>：玻璃/水晶碎裂声。</summary>
                public static SoundStyle Shatter => SoundID.Shatter;

                /// <summary><see cref="SoundID.Tink"/>：金属/矿石敲击叮当声。</summary>
                public static SoundStyle Tink => SoundID.Tink;

                /// <summary><see cref="SoundID.Dig"/>：方块挖掘/撞击声。</summary>
                public static SoundStyle Dig => SoundID.Dig;

                /// <summary><see cref="SoundID.DoubleJump"/>：二段跳气流声。</summary>
                public static SoundStyle DoubleJump => SoundID.DoubleJump;

                /// <summary><see cref="SoundID.Unlock"/>：开锁/机关解锁声。</summary>
                public static SoundStyle Unlock => SoundID.Unlock;

                /// <summary><see cref="SoundID.MenuOpen"/>：UI 菜单打开声。</summary>
                public static SoundStyle MenuOpen => SoundID.MenuOpen;

                /// <summary><see cref="SoundID.MenuClose"/>：UI 菜单关闭声。</summary>
                public static SoundStyle MenuClose => SoundID.MenuClose;

                /// <summary><see cref="SoundID.MenuTick"/>：UI 悬停/点击嘀嗒声。</summary>
                public static SoundStyle MenuTick => SoundID.MenuTick;

                /// <summary><see cref="SoundID.CoinPickup"/>：拾取硬币声。</summary>
                public static SoundStyle CoinPickup => SoundID.CoinPickup;

                /// <summary><see cref="SoundID.ResearchComplete"/>：旅途研究完成提示音。</summary>
                public static SoundStyle ResearchComplete => SoundID.ResearchComplete;

                /// <summary><see cref="SoundID.AchievementComplete"/>：成就达成提示音。</summary>
                public static SoundStyle AchievementComplete => SoundID.AchievementComplete;
            }
        }

        /// <summary>
        /// 原版泰拉瑞亚全部 5 套字体（已核对 <c>Terraria/Content/Fonts/*.xnb</c>）。
        /// </summary>
        public static partial class Fonts
        {
            /// <summary>常规 UI / 鼠标提示字体（<c>Terraria/Fonts/Mouse_Text</c>）。</summary>
            public const string MouseTextPath = "Terraria/Fonts/Mouse_Text";
            private static Asset<DynamicSpriteFont>? _mouseText;
            public static Asset<DynamicSpriteFont> MouseText => _mouseText ??= ModContent.Request<DynamicSpriteFont>(MouseTextPath);

            /// <summary>物品堆叠数字字体（<c>Terraria/Fonts/Item_Stack</c>）。</summary>
            public const string ItemStackPath = "Terraria/Fonts/Item_Stack";
            private static Asset<DynamicSpriteFont>? _itemStack;
            public static Asset<DynamicSpriteFont> ItemStack => _itemStack ??= ModContent.Request<DynamicSpriteFont>(ItemStackPath);

            /// <summary>标题/死亡大字字体（<c>Terraria/Fonts/Death_Text</c>）。</summary>
            public const string DeathTextPath = "Terraria/Fonts/Death_Text";
            private static Asset<DynamicSpriteFont>? _deathText;
            public static Asset<DynamicSpriteFont> DeathText => _deathText ??= ModContent.Request<DynamicSpriteFont>(DeathTextPath);

            /// <summary>普通战斗伤害数字字体（<c>Terraria/Fonts/Combat_Text</c>）。</summary>
            public const string CombatTextPath = "Terraria/Fonts/Combat_Text";
            private static Asset<DynamicSpriteFont>? _combatText;
            public static Asset<DynamicSpriteFont> CombatText => _combatText ??= ModContent.Request<DynamicSpriteFont>(CombatTextPath);

            /// <summary>暴击战斗伤害数字字体（<c>Terraria/Fonts/Combat_Crit</c>）。</summary>
            public const string CombatCritPath = "Terraria/Fonts/Combat_Crit";
            private static Asset<DynamicSpriteFont>? _combatCrit;
            public static Asset<DynamicSpriteFont> CombatCrit => _combatCrit ??= ModContent.Request<DynamicSpriteFont>(CombatCritPath);

            [EditorBrowsable(EditorBrowsableState.Never)]
            internal static void __Unload()
            {
                _mouseText = null;
                _itemStack = null;
                _deathText = null;
                _combatText = null;
                _combatCrit = null;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal static void UnloadAll()
        {
            Vfx.__Unload();
            Fonts.__Unload();
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class VanillaAssetsUnloader : ILoadable
    {
        public void Load(Mod mod) { }

        public void Unload() => VanillaAssets.UnloadAll();
    }
}
