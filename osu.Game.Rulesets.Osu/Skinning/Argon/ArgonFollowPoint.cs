// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Game.Graphics;
using osuTK;

namespace osu.Game.Rulesets.Osu.Skinning.Argon
{
    /// <summary>
    /// Two overlapping chevrons, drawn as a single leaf drawable.
    /// </summary>
    /// <remarks>
    /// This was previously a composite holding two <see cref="SpriteIcon"/>s. Dense maps keep several thousand
    /// follow points alive at once, so those two extra drawables per point were a large share of both the update
    /// tree and of draw node generation. Both chevrons are the same glyph, so one draw node can emit both quads.
    /// </remarks>
    public partial class ArgonFollowPoint : Drawable, ITexturedShaderDrawable
    {
        private const float icon_size = 8;
        private const float second_icon_offset = 4;

        public IShader? TextureShader { get; private set; }

        private static readonly IconUsage icon = FontAwesome.Solid.ChevronRight;

        private Texture? texture;

        public ArgonFollowPoint()
        {
            Blending = BlendingParameters.Additive;

            Colour = ColourInfo.GradientVertical(Colour4.FromHex("FC618F"), Colour4.FromHex("BB1A41"));

            // matches what auto-sizing produced for the two 8x8 icons, the second offset by 4.
            Size = new Vector2(icon_size + second_icon_offset, icon_size);
        }

        [BackgroundDependencyLoader]
        private void load(FontStore fonts, ShaderManager shaders)
        {
            TextureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
            texture = fonts.Get(icon.FontName, icon.Icon)?.Texture;
        }

        protected override DrawNode CreateDrawNode() => new ArgonFollowPointDrawNode(this);

        private class ArgonFollowPointDrawNode : TexturedShaderDrawNode
        {
            protected new ArgonFollowPoint Source => (ArgonFollowPoint)base.Source;

            public ArgonFollowPointDrawNode(ArgonFollowPoint source)
                : base(source)
            {
            }

            private Texture? texture;

            private Quad backQuad;
            private Quad frontQuad;
            private ColourInfo backColour;
            private ColourInfo frontColour;

            private Texture? geometryTexture;
            private Vector2 geometryDrawSize;
            private RectangleF backRect;
            private RectangleF frontRect;
            private Quad backRelativeQuad;
            private Quad frontRelativeQuad;

            public override void ApplyState()
            {
                base.ApplyState();

                texture = Source.texture;

                if (texture == null)
                    return;

                Vector2 drawSize = Source.DrawSize;

                if (geometryTexture != texture || geometryDrawSize != drawSize)
                {
                    geometryTexture = texture;
                    geometryDrawSize = drawSize;

                    // replicates `SpriteIcon`'s fit-and-centre of the glyph within an `icon_size` box.
                    float scale = Math.Min(icon_size / texture.Width, icon_size / texture.Height);
                    Vector2 glyphSize = texture.Size * scale;
                    Vector2 inset = (new Vector2(icon_size) - glyphSize) * 0.5f;

                    backRect = new RectangleF(inset.X, inset.Y, glyphSize.X, glyphSize.Y);
                    frontRect = new RectangleF(inset.X + second_icon_offset, inset.Y, glyphSize.X, glyphSize.Y);
                    backRelativeQuad = toRelative(backRect, drawSize);
                    frontRelativeQuad = toRelative(frontRect, drawSize);
                }

                backQuad = Source.ToScreenSpace(backRect);
                frontQuad = Source.ToScreenSpace(frontRect);

                // each chevron takes the slice of the parent's vertical gradient that covers it, exactly as
                // the separate child drawables used to.
                backColour = DrawColourInfo.Colour.Interpolate(backRelativeQuad);
                frontColour = DrawColourInfo.Colour.Interpolate(frontRelativeQuad);

                // the rear chevron was additionally tinted by its own colour.
                backColour.ApplyChild(OsuColour.Gray(0.2f));
            }

            private static Quad toRelative(RectangleF rect, Vector2 drawSize) =>
                new Quad(rect.X / drawSize.X, rect.Y / drawSize.Y, rect.Width / drawSize.X, rect.Height / drawSize.Y);

            protected override void Draw(IRenderer renderer)
            {
                base.Draw(renderer);

                if (texture?.Available != true)
                    return;

                BindTextureShader(renderer);

                renderer.DrawQuad(texture, backQuad, backColour);
                renderer.DrawQuad(texture, frontQuad, frontColour);

                UnbindTextureShader(renderer);
            }
        }
    }
}
