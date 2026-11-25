namespace PCL.UI.Bridges;

/// <summary>区域下载与整合包功能桥接（已移除中国大陆游戏下载/补全/整合包安装区域限制）。</summary>
public sealed class RestrictedFeaturesBridge : IRestrictedFeatures
{
    public bool IsRestricted => false;

    public bool Check(string feature) => true;

    public void ThrowIfRestricted(string feature)
    {
    }
}