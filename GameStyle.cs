using System;
using BepInEx.Logging;
using FFG.Common;
using FFG.JIME;
using HarmonyLib;
using UnityEngine;

namespace JiMEOverlay
{
    // Borrows the look of the game's own message popup at runtime: its frame sprite (drawn 9-sliced from the
    // game's texture atlas, so corners stay crisp at any size), its font and its text colour.
    // Nothing is copied to disk; if any piece can't be found, callers fall back to the overlay's own style.
    public class GameStyle
    {
        private static readonly AccessTools.FieldRef<MessagePopup, UILabel> TextLabelRef =
            AccessTools.FieldRefAccess<MessagePopup, UILabel>("_textLabel");
        private static readonly AccessTools.FieldRef<MessagePopup, UISprite> BackgroundRef =
            AccessTools.FieldRefAccess<MessagePopup, UISprite>("_background");

        private readonly ManualLogSource _log;
        private bool _loaded;
        private bool _failed;

        public Font Font { get; private set; }
        public Color TextColor { get; private set; } = Color.white;
        public Texture FrameTexture { get; private set; }
        public Color FrameTint { get; private set; } = Color.white;
        // Sprite position and borders in atlas pixels (NGUI measures from the atlas's top-left corner).
        private Rect _frameRect;
        private RectOffset _frameBorder;

        public bool HasFrame => FrameTexture != null && _frameBorder != null;

        public GameStyle(ManualLogSource log)
        {
            _log = log;
        }

        // Loads once the game's UI exists; cheap to call every frame.
        public bool TryLoad()
        {
            if (_loaded || _failed)
            {
                return _loaded;
            }
            try
            {
                if (!SingletonBehaviour<UI_Game>.IsInitialized)
                {
                    return false;
                }
                var popup = SingletonBehaviour<UI_Game>.Instance.MessageDialog;
                if (popup == null)
                {
                    return false;
                }

                var label = TextLabelRef(popup);
                if (label != null)
                {
                    Font = label.trueTypeFont != null ? label.trueTypeFont : label.font != null ? label.font.dynamicFont : null;
                    TextColor = label.color;
                }

                var background = BackgroundRef(popup);
                var sprite = background != null ? background.GetAtlasSprite() : null;
                var atlas = background != null ? background.atlas : null;
                if (sprite != null && atlas != null && atlas.texture != null)
                {
                    FrameTexture = atlas.texture;
                    FrameTint = background.color;
                    _frameRect = new Rect(sprite.x, sprite.y, sprite.width, sprite.height);
                    _frameBorder = new RectOffset(sprite.borderLeft, sprite.borderRight, sprite.borderTop, sprite.borderBottom);
                }

                _loaded = true;
                _log.LogInfo($"Tooltip style: font {(Font != null ? Font.name : "default")}, frame {(HasFrame ? background.spriteName : "none")}.");
            }
            catch (Exception e)
            {
                _failed = true;
                _log.LogWarning($"Could not borrow the game's popup style; using the overlay style for tooltips: {e.Message}");
            }
            return _loaded;
        }

        // Whether the borrowed font can draw this character (the game's icons are private-use characters).
        public bool CanDraw(char c) => Font != null && Font.HasCharacter(c);

        // Draws the popup frame 9-sliced into rect; borderScale scales the corner/edge thickness.
        public void DrawFrame(Rect rect, float borderScale)
        {
            var tex = FrameTexture;
            float tw = tex.width, th = tex.height;
            float sl = _frameBorder.left, sr = _frameBorder.right, st = _frameBorder.top, sb = _frameBorder.bottom;
            float dl = sl * borderScale, dr = sr * borderScale, dt = st * borderScale, db = sb * borderScale;

            // Never let the borders overlap on small tooltips.
            var fit = Mathf.Min(1f, rect.width / Mathf.Max(1f, dl + dr), rect.height / Mathf.Max(1f, dt + db));
            dl *= fit; dr *= fit; dt *= fit; db *= fit;

            float[] srcX = { _frameRect.x, _frameRect.x + sl, _frameRect.xMax - sr, _frameRect.xMax };
            float[] srcY = { _frameRect.y, _frameRect.y + st, _frameRect.yMax - sb, _frameRect.yMax };
            float[] dstX = { rect.x, rect.x + dl, rect.xMax - dr, rect.xMax };
            float[] dstY = { rect.y, rect.y + dt, rect.yMax - db, rect.yMax };

            var oldColor = GUI.color;
            GUI.color = FrameTint * oldColor;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    var dst = Rect.MinMaxRect(dstX[col], dstY[row], dstX[col + 1], dstY[row + 1]);
                    var srcW = srcX[col + 1] - srcX[col];
                    var srcH = srcY[row + 1] - srcY[row];
                    if (dst.width <= 0f || dst.height <= 0f || srcW <= 0f || srcH <= 0f)
                    {
                        continue;
                    }
                    // Unity texture coordinates start at the bottom-left, NGUI's at the top-left.
                    var uv = new Rect(srcX[col] / tw, 1f - srcY[row + 1] / th, srcW / tw, srcH / th);
                    GUI.DrawTextureWithTexCoords(dst, tex, uv);
                }
            }
            GUI.color = oldColor;
        }
    }
}
