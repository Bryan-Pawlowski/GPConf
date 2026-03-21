using Google.Protobuf;

namespace GPConf.Utilities;

public class CCUtils
{
    public static ByteString CreateUniqueId()
    {
        return Google.Protobuf.ByteString.CopyFrom(System.Guid.NewGuid().ToByteArray());
    }
}