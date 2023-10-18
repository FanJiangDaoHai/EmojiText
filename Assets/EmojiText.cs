using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Pool;
using UnityEngine.UI;

namespace UI
{
    public class EmojiText : Text, IPointerClickHandler
    {
        private static readonly Regex m_SpriteTagRegex =
            new Regex(@"<quad.*?/>", RegexOptions.Singleline);

        private static readonly Regex m_DisplayKeyRegex =
            new Regex(@"displaykey=([\d,]+)", RegexOptions.Singleline);

        private static readonly Regex m_SizeRegex =
            new Regex(@"size=(\d*\.?\d+%?)", RegexOptions.Singleline);

        private static readonly Regex m_WidthRegex =
            new Regex(@"width=(\d*\.?\d+%?)", RegexOptions.Singleline);

        private static readonly Regex m_HeightRegex =
            new Regex(@"height=(\d*\.?\d+%?)", RegexOptions.Singleline);

        private static readonly Regex m_QuadRemoveRegex =
            new Regex(
                @"<b>|</b>|<i>|</i>|<size=.*?>|</size>|<color=.*?>|</color>|<material=.*?>|</material>|<a href=([^>\n\s]+)>|</a>|\s",
                RegexOptions.Singleline);

        private static readonly Regex m_HrefRemoveRegex =
            new Regex(
                @"<b>|</b>|<i>|</i>|<size=.*?>|</size>|<color=.*?>|</color>|<material=.*?>|</material>|<quad.*?/>|\s",
                RegexOptions.Singleline);

        private static readonly Regex s_HrefRegex =
            new Regex(@"<ahref=([^>\n\s]+)>(.*?)(</a>)", RegexOptions.Singleline);

        readonly UIVertex[] m_TempVerts = new UIVertex[4];

        protected override void OnPopulateMesh(VertexHelper toFill)
        {
            if (font == null)
                return;

            m_HaveChange = true;

            UpdateQuadInfo();
            RefreshHrefInfo();
            m_DisableFontTextureRebuiltCallback = true;

            Vector2 extents = rectTransform.rect.size;

            var settings = GetGenerationSettings(extents);
            cachedTextGenerator.PopulateWithErrors(text, settings, gameObject);

            // Apply the offset to the vertices
            IList<UIVertex> verts = cachedTextGenerator.verts;
            float unitsPerPixel = 1 / pixelsPerUnit;
            int vertCount = verts.Count;

            // We have no verts to process just return (case 1037923)
            if (vertCount <= 0)
            {
                toFill.Clear();
                return;
            }

            Vector2 roundingOffset = new Vector2(verts[0].position.x, verts[0].position.y) * unitsPerPixel;
            roundingOffset = PixelAdjustPoint(roundingOffset) - roundingOffset;
            toFill.Clear();
            for (int i = 0; i < vertCount; ++i)
            {
                var index = i / 4;
                if (m_QuadIndexDict.TryGetValue(index, out var imageIndex))
                {
                    var imageInfo = m_QuadInfos[imageIndex];
                    if (i % 4 != 3) continue;
                    var pos = verts[i].position;

                    pos.x += roundingOffset.x;
                    pos.y += roundingOffset.y;
                    var bestScale = resizeTextForBestFit
                        ? (float)cachedTextGenerator.fontSizeUsedForBestFit / fontSize
                        : 1;
                    var realFontSize = resizeTextForBestFit ? cachedTextGenerator.fontSizeUsedForBestFit : fontSize;
                    //探索得经验公式，不是精确的，但是可以解决大部分情况（本身属于是Unity LineSpace Bug）
                    var offsetY = ((imageInfo.sizeY * bestScale - realFontSize - 2) * 0.2f + 1) *
                                  unitsPerPixel * lineSpacing;

                    imageInfo.RectInfo.OffsetY = Mathf.Max(0, offsetY);
                    imageInfo.RectInfo.VertPosY = verts[i].position.y;
                    imageInfo.RectInfo.Pos = new Vector2(pos.x + imageInfo.sizeX / 2 * bestScale,
                        pos.y + imageInfo.sizeY / 2 * bestScale) * unitsPerPixel;
                    imageInfo.RectInfo.Size = new Vector2(imageInfo.sizeX * bestScale, imageInfo.sizeY * bestScale) *
                                              unitsPerPixel;
                }
            }

            ResetOffsetY();
            for (int i = 0; i < vertCount; i++)
            {
                var index = i / 4;
                if (!m_QuadIndexDict.ContainsKey(index))
                {
                    int tempVertsIndex = i & 3;
                    m_TempVerts[tempVertsIndex] = verts[i];
                    m_TempVerts[tempVertsIndex].position *= unitsPerPixel;
                    m_TempVerts[tempVertsIndex].position.x += roundingOffset.x;
                    m_TempVerts[tempVertsIndex].position.y += roundingOffset.y;
                    if (tempVertsIndex == 3)
                    {
                        var offsetY = GetOffsetY(m_TempVerts[tempVertsIndex].position.y);
                        for (var index1 = 0; index1 < m_TempVerts.Length; index1++)
                        {
                            m_TempVerts[index1].position.y -= offsetY;
                        }

                        toFill.AddUIVertexQuad(m_TempVerts);
                    }
                }
            }


            m_DisableFontTextureRebuiltCallback = false;
            RefreshHrefBox(toFill);
        }

        private void LateUpdate()
        {
            UpdateQuad();
            UpdateGif();
        }

        #region Quad

        private float GetOffsetY(float y)
        {
            for (var i = 0; i < m_QuadInfos.Count; i++)
            {
                var info = m_QuadInfos[i];
                if (Mathf.Abs(info.RectInfo.VertPosY - y) < fontSize)
                {
                    return info.RectInfo.OffsetY;
                }
            }

            return 0;
        }

        /// <summary>
        /// 选取同一行图片中最大的offsetY
        /// </summary>
        private void ResetOffsetY()
        {
            var realFontSize = resizeTextForBestFit ? cachedTextGenerator.fontSizeUsedForBestFit : fontSize;
            for (var i = 0; i < m_QuadInfos.Count; i++)
            {
                for (int j = i + 1; j < m_QuadInfos.Count; j++)
                {
                    var info1 = m_QuadInfos[i];
                    var info2 = m_QuadInfos[j];
                    if (Mathf.Abs(info2.RectInfo.VertPosY - info1.RectInfo.VertPosY) < realFontSize)
                    {
                        var max = info1.RectInfo.OffsetY > info2.RectInfo.OffsetY
                            ? info1.RectInfo.OffsetY
                            : info2.RectInfo.OffsetY;
                        info1.RectInfo.OffsetY = max;
                        info2.RectInfo.OffsetY = max;
                    }
                }
            }
        }


        /// <summary>
        /// 刷新图片
        /// </summary>
        void UpdateQuad()
        {
            if (!supportRichText)
            {
                for (int i = m_ImageObjects.Count - 1; i > -1; i--)
                {
                    if (Application.isEditor)
                    {
                        DestroyImmediate(m_ImageObjects[i].gameObject);
                    }
                    else
                    {
                        Destroy(m_ImageObjects[i].gameObject);
                    }

                    m_ImageObjects.RemoveAt(i);
                }

                return;
            }

            if (!m_HaveChange)
                return;
            var count = m_SpriteTagRegex.Matches(text).Count;
            GetComponentsInChildren<Image>(true, m_ImageObjects);
            if (m_ImageObjects.Count < count)
            {
                for (var i = m_ImageObjects.Count; i < count; i++)
                {
                    DefaultControls.Resources resources = new DefaultControls.Resources();
                    GameObject go = DefaultControls.CreateImage(resources);

                    go.layer = gameObject.layer;

                    RectTransform rt = go.transform as RectTransform;

                    if (rt)
                    {
                        rt.SetParent(rectTransform);
                        rt.anchoredPosition3D = Vector3.zero;
                        rt.localRotation = Quaternion.identity;
                        rt.localScale = Vector3.one;
                    }

                    Image imgCom = go.GetComponent<Image>();
                    imgCom.enabled = false;

                    m_ImageObjects.Add(imgCom);
                }
            }

            for (int i = m_ImageObjects.Count - 1; i > -1; i--)
            {
                if (i >= count)
                {
                    if (Application.isEditor)
                    {
                        DestroyImmediate(m_ImageObjects[i].gameObject);
                    }
                    else
                    {
                        Destroy(m_ImageObjects[i].gameObject);
                    }

                    m_ImageObjects.RemoveAt(i);
                }
            }

            for (var i = 0; i < m_QuadInfos.Count; i++)
            {
                var imagesValue = m_QuadInfos[i];
                var image = m_ImageObjects[i];
                image.enabled = true;
                image.rectTransform.sizeDelta = imagesValue.RectInfo.Size;
                image.rectTransform.anchoredPosition =
                    imagesValue.RectInfo.Pos - Vector2.up * imagesValue.RectInfo.OffsetY;
                if (!imagesValue.IsGif)
                {
                    image.sprite = imagesValue.GetFirstSprite();
                }
            }

            m_HaveChange = false;
        }

        /// <summary>
        /// 刷新Quad信息
        /// </summary>
        void UpdateQuadInfo()
        {
            m_QuadIndexDict.Clear();
            if (!supportRichText) return;
            var totalLen = 0;
            var newText = m_QuadRemoveRegex.Replace(text, "");
            var matches = m_SpriteTagRegex.Matches(newText);
            while (m_QuadInfos.Count < matches.Count)
            {
                m_QuadInfos.Add(m_ImageInfoPool.Get());
            }

            while (m_QuadInfos.Count > matches.Count)
            {
                m_ImageInfoPool.Release(m_QuadInfos[^1]);
                m_QuadInfos.RemoveAt(m_QuadInfos.Count - 1);
            }

            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var index = match.Index - totalLen;
                totalLen += match.Length - 1;
                var matchValue = match.Value;
                var displayKeyMatch = m_DisplayKeyRegex.Match(matchValue);
                var sizeMatch = m_SizeRegex.Match(matchValue);
                var size = sizeMatch.Success ? float.Parse(sizeMatch.Groups[1].Value) : fontSize;
                var widthMatch = m_WidthRegex.Match(matchValue);
                var width = widthMatch.Success ? float.Parse(widthMatch.Groups[1].Value) : 1;
                var heightMatch = m_HeightRegex.Match(matchValue);
                var height = heightMatch.Success ? float.Parse(heightMatch.Groups[1].Value) : 1;
                var imageInfo = m_QuadInfos[i];
                imageInfo.Size = size;
                imageInfo.Width = width;
                imageInfo.Height = height;
                if (displayKeyMatch.Success)
                {
                    var displayKeys = displayKeyMatch.Groups[1].Value.Split(',');
                    foreach (var displayKey in displayKeys)
                    {
                        imageInfo.Add(displayKey);
                    }
                }

                m_QuadIndexDict.Add(index, i);
            }
        }

        private void UpdateGif()
        {
            m_Time += Time.deltaTime;
            if (m_Time > m_GifInvadeTime)
            {
                for (var index = 0; index < m_QuadInfos.Count; index++)
                {
                    var imagesValue = m_QuadInfos[index];
                    var image = m_ImageObjects[index];
                    if (imagesValue.IsGif)
                    {
                        image.sprite = imagesValue.GetNextSprite();
                    }
                }

                m_Time = 0;
            }
        }


        private float m_Time = 0;
        private const float m_GifInvadeTime = 0.1f;
        private readonly Dictionary<int, int> m_QuadIndexDict = new Dictionary<int, int>();
        private readonly List<QuadInfo> m_QuadInfos = new List<QuadInfo>();
        private readonly List<Image> m_ImageObjects = new List<Image>();
        private bool m_HaveChange;

        private static readonly ObjectPool<QuadInfo> m_ImageInfoPool =
            new ObjectPool<QuadInfo>(() => new QuadInfo(), null, info => info.Clear());

        private class QuadInfo
        {
            public float Size;
            public float Width;
            public float Height;
            public float sizeX => Size * (Width / Height);
            public float sizeY => Size;

            public ImageRectInfo RectInfo = new ImageRectInfo();

            public void Add(string displayKey)
            {
                m_DisplayKeys.Add(displayKey);
                Sprite sprite = null;
                if (Application.isEditor)
                {
                    sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"Assets/Resources/{displayKey}.png");
                }
                else if (Application.isPlaying)
                {
                    sprite = Resources.Load<Sprite>(displayKey);
                }

                if (sprite != null)
                {
                    m_Sprites.Add(sprite);
                }
            }

            public void Clear()
            {
                m_DisplayKeys.Clear();
                m_Sprites.Clear();
                m_Index = 0;
            }

            public Sprite GetNextSprite()
            {
                if (m_Sprites.Count == 0) return null;
                m_Index++;
                if (m_Index >= m_Sprites.Count)
                {
                    m_Index = 0;
                }

                return m_Sprites[m_Index];
            }

            public Sprite GetFirstSprite()
            {
                if (m_Sprites.Count == 0) return null;
                return m_Sprites[0];
            }

            public bool IsGif => m_Sprites.Count > 1;

            private List<string> m_DisplayKeys = new List<string>();
            private List<Sprite> m_Sprites = new List<Sprite>();

            private int m_Index = 0;
        }


        private class ImageRectInfo
        {
            public float OffsetY;
            public float VertPosY;
            public Vector2 Size;
            public Vector2 Pos;
        }

        #endregion


        #region Href

        private class HrefInfo
        {
            public int startIndex;

            public int endIndex;

            public string name;

            public readonly List<Rect> boxes = new List<Rect>();
        }


        /// <summary>
        /// 超链接信息列表
        /// </summary>
        private readonly List<HrefInfo> m_HrefInfos = new List<HrefInfo>();

        private static readonly ObjectPool<HrefInfo> m_HrefInfoPool =
            new ObjectPool<HrefInfo>(() => new HrefInfo(), null, null);


        //<a href=https://blog.csdn.net/weixin_43737238/article/details/104377121>超链接</a>
        private void RefreshHrefBox(VertexHelper toFill)
        {
            UIVertex vert = new UIVertex();

            // 处理超链接包围框
            foreach (var hrefInfo in m_HrefInfos)
            {
                hrefInfo.boxes.Clear();
                if (hrefInfo.startIndex >= toFill.currentVertCount)
                {
                    continue;
                }

                // 将超链接里面的文本顶点索引坐标加入到包围框
                toFill.PopulateUIVertex(ref vert, hrefInfo.startIndex);
                var pos = vert.position;
                var bounds = new Bounds(pos, Vector3.zero);
                for (int i = hrefInfo.startIndex, m = hrefInfo.endIndex; i < m; i++)
                {
                    if (i >= toFill.currentVertCount)
                    {
                        break;
                    }

                    toFill.PopulateUIVertex(ref vert, i);
                    pos = vert.position;
                    if (pos.x < bounds.min.x) // 换行重新添加包围框
                    {
                        hrefInfo.boxes.Add(new Rect(bounds.min, bounds.size));
                        bounds = new Bounds(pos, Vector3.zero);
                    }
                    else
                    {
                        bounds.Encapsulate(pos); // 扩展包围框
                    }
                }

                hrefInfo.boxes.Add(new Rect(bounds.min, bounds.size));
            }
        }

        /// <summary>
        /// 获取超链接解析后的最后输出文本
        /// </summary>
        /// <returns></returns>
        private void RefreshHrefInfo()
        {
            var outputText = m_HrefRemoveRegex.Replace(text, "");
            var matches = s_HrefRegex.Matches(outputText);
            while (m_HrefInfos.Count < matches.Count)
            {
                m_HrefInfos.Add(m_HrefInfoPool.Get());
            }

            while (m_HrefInfos.Count > matches.Count)
            {
                m_HrefInfoPool.Release(m_HrefInfos[^1]);
                m_HrefInfos.RemoveAt(m_QuadInfos.Count - 1);
            }

            for (var index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                var group = match.Groups[1];

                m_HrefInfos[index].startIndex = match.Index * 4; // 超链接里的文本起始顶点索引
                m_HrefInfos[index].endIndex = (match.Index + match.Groups[2].Length - 1) * 4 + 3;
                m_HrefInfos[index].name = group.Value;
            }
        }

        /// <summary>
        /// 点击事件检测是否点击到超链接文本
        /// </summary>
        /// <param name="eventData"></param>
        public void OnPointerClick(PointerEventData eventData)
        {
            Vector2 lp = Vector2.zero;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, eventData.position,
                eventData.pressEventCamera, out lp);

            foreach (var hrefInfo in m_HrefInfos)
            {
                var boxes = hrefInfo.boxes;
                for (var i = 0; i < boxes.Count; ++i)
                {
                    if (boxes[i].Contains(lp))
                    {
                        Application.OpenURL(hrefInfo.name);
                        return;
                    }
                }
            }
        }

        #endregion
    }
}