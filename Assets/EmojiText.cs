using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class EmojiText : Text
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

    readonly UIVertex[] m_TempVerts = new UIVertex[4];

    protected override void OnPopulateMesh(VertexHelper toFill)
    {
        if (font == null)
            return;

        m_HaveChange = true;

        UpdateImageInfo();
        m_ImageRectInfos.Clear();
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
            if (m_Images.TryGetValue(index, out var imageInfo))
            {
                if (i % 4 != 3) continue;
                var pos = verts[i].position;

                pos.x += roundingOffset.x;
                pos.y += roundingOffset.y;
                var bestScale = resizeTextForBestFit
                    ? (float)cachedTextGenerator.fontSizeUsedForBestFit / fontSize
                    : 1;
                var realFontSize = resizeTextForBestFit ? cachedTextGenerator.fontSizeUsedForBestFit : fontSize;
                var offsetY = ((imageInfo.sizeY * bestScale - realFontSize - 2) * 0.2f + 1) *
                              unitsPerPixel * lineSpacing;
                m_ImageRectInfos.Add(new ImageRectInfo()
                {
                    OffsetY = offsetY,
                    VertPosY = verts[i].position.y,
                    Pos = new Vector2(pos.x + imageInfo.sizeX / 2 * bestScale,
                        pos.y + imageInfo.sizeY / 2 * bestScale) * unitsPerPixel,
                    Size = new Vector2(imageInfo.sizeX * bestScale, imageInfo.sizeY * bestScale) * unitsPerPixel,
                });
            }
        }

        ResetOffsetY();
        for (int i = 0; i < vertCount; i++)
        {
            var index = i / 4;
            if (!m_Images.ContainsKey(index))
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
    }

    private float GetOffsetY(float y)
    {
        for (var i = 0; i < m_ImageRectInfos.Count; i++)
        {
            var info = m_ImageRectInfos[i];
            if (Mathf.Abs(info.VertPosY - y) < fontSize)
            {
                return info.OffsetY;
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
        for (var i = 0; i < m_ImageRectInfos.Count; i++)
        {
            for (int j = i + 1; j < m_ImageRectInfos.Count; j++)
            {
                var info1 = m_ImageRectInfos[i];
                var info2 = m_ImageRectInfos[j];
                if (Mathf.Abs(info2.VertPosY - info1.VertPosY) < realFontSize)
                {
                    var max = info1.OffsetY > info2.OffsetY ? info1.OffsetY : info2.OffsetY;
                    info1.OffsetY = max;
                    info2.OffsetY = max;
                    m_ImageRectInfos[i] = info1;
                    m_ImageRectInfos[j] = info2;
                }
                
            }
        }
    }


    private void LateUpdate()
    {
        UpdateQuad();
    }

    void UpdateQuad()
    {
        // if (!m_HaveChange)
        //     return;
        var count = m_SpriteTagRegex.Matches(text).Count;
        GetComponentsInChildren<Image>(true, m_ImagesPool);
        if (m_ImagesPool.Count < count)
        {
            for (var i = m_ImagesPool.Count; i < count; i++)
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

                m_ImagesPool.Add(imgCom);
            }
        }

        for (int i = m_ImagesPool.Count - 1; i > -1; i--)
        {
            if (i >= count)
            {
                if (Application.isEditor)
                {
                    DestroyImmediate(m_ImagesPool[i].gameObject);
                }
                else
                {
                    Destroy(m_ImagesPool[i].gameObject);
                }

                m_ImagesPool.RemoveAt(i);
            }
        }

        foreach (var imagesValue in m_Images.Values)
        {
            var index = imagesValue.PoolIndex;
            var image = m_ImagesPool[index];
            image.enabled = true;
            image.rectTransform.sizeDelta = m_ImageRectInfos[index].Size;
            image.rectTransform.anchoredPosition = m_ImageRectInfos[index].Pos - Vector2.up * m_ImageRectInfos[index].OffsetY;
        }

        m_HaveChange = false;
    }

    void UpdateImageInfo()
    {
        m_Images.Clear();
        var totalLen = 0;
        var matches = m_SpriteTagRegex.Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var index = match.Index - totalLen;
            totalLen += match.Length - 1;
            var matchValue = match.Value;
            var displayKeys = m_DisplayKeyRegex.Match(matchValue).Groups[1].Value.Split(',').ToList();
            var sizeMatch = m_SizeRegex.Match(matchValue);
            var size = sizeMatch.Success ? float.Parse(sizeMatch.Groups[1].Value) : fontSize;
            var widthMatch = m_WidthRegex.Match(matchValue);
            var width = widthMatch.Success ? float.Parse(widthMatch.Groups[1].Value) : 1;
            var heightMatch = m_HeightRegex.Match(matchValue);
            var height = heightMatch.Success ? float.Parse(heightMatch.Groups[1].Value) : 1;
            var imageInfo = new ImageInfo()
            {
                DisplayKeys = displayKeys,
                Size = size,
                Width = width,
                Height = height,
                PoolIndex = i,
            };
            m_Images.Add(index, imageInfo);
        }
    }


    private Dictionary<int, ImageInfo> m_Images = new Dictionary<int, ImageInfo>();
    private List<Image> m_ImagesPool = new List<Image>();
    private List<ImageRectInfo> m_ImageRectInfos = new List<ImageRectInfo>();
    private bool m_HaveChange;

    private struct ImageInfo
    {
        public List<string> DisplayKeys;
        public float Size;
        public float Width;
        public float Height;
        public int PoolIndex;
        public float sizeX => Size * (Width / Height);
        public float sizeY => Size;
    }

    private struct ImageRectInfo
    {
        public float OffsetY;
        public float VertPosY;
        public Vector2 Size;
        public Vector2 Pos;
    }
}