namespace HeliVMS.Licensing.Tests;

/// <summary>
/// 開發／CI 專用測試金鑰對（與內嵌於產品的測試公鑰成對）。
/// 僅供自動化測試使用，正式授權之私鑰永不進入 repo（§19 金鑰管理）。
/// </summary>
internal static class TestKeys
{
    public const string PrivateKeyPem =
        """
        -----BEGIN PRIVATE KEY-----
        MIIEvwIBADANBgkqhkiG9w0BAQEFAASCBKkwggSlAgEAAoIBAQDU7bMiz6GmXJIj
        SRl5wCssfiDT6StejlJsMAPk4wmEYBwUR4740KvOLaPP+yi7hvC8rBWUVnxxFQzb
        XF2yAUQGLdZ2wBYFm6+Rkq3Xe9MeKdf/cw4YXX1Y8rObTBSoXLiDlDtz8FUhjdUm
        MIUWfGRMaRYX+J28FTGw2pgGtZrouV/puTXG/eWj19keKUKjqfTUEN8vk9zE4wps
        BoIs+Li1/X/ZEv5rL3du9FDHCGbh2kkkcihgjwaRrn2FRWEucb88ld5pyg4xTiG7
        XHMcFkOQc11Majcvh2AjwTrU0/pR3FFISORQV9RV0WWinFrDIkt9Ug1Qm8YgMl4P
        13OIWoklAgMBAAECggEAYFoPrbj3FSjemEkD1iw3XTLV+A6TKX6NnZc8q95w+A3l
        pueassz6cZoFfp/QlstBNJ9KRI2+Ug2Me9cYLuyTs5gabDIPxQnmMKGHOPM3XXbh
        3x5ZSn1Ds3RgAe4zitwqQqBZJuWiIETmJxndn4c6e7kx3jcKaYnNqpHAKYkUSCqG
        dkHcZgn/HxmKnAan84GvRhBU+XwNCsnKCAk1q3OrM8RfGA2VILMUjtnNVXMEBBPm
        xGOhvBpMw+i7sQxABeJKGg5PPXNLnA/09EinAJCioHKSBiAvj039kUfnln2dR9/x
        vXmEuTDNbNG772xsk8ndyB6zJqv58Q3LfriO7l8lIQKBgQD7OfNlcVxpjwcU9ISz
        VWdRlr+meyDei7ewkJWXG4gq23RfdePb5JjcQv0Wqbhy4/XFyTgi28Pv9dBGKglB
        Mnf5z9QSXi4PEiduUIJ6P5zDf9iqua2iQkzFi/Vj5MrXRN02lBzUyf6OYbZ5QDTc
        sHOmjBR6l0A4WZaC7aA889oKcwKBgQDY+XST+msXUMCpLoj4me8w4XkKw+jR1CWT
        2cF11eZPf6iGAN+akHY9ybWngnrlFTwxYaDdrJrycAmWpEye5TxGUdIVbA/vy9X2
        b8Z4ICe1F6wl5Rq6f7klaCrs+y2i5lkBLKawi8Gzo5cViIV/lyZg1V8JzbzIbqFp
        FDsZyrrABwKBgQDq4oG4xMZcQ0ehxgQUD7NrywAgzVor+IGj6WqTP0COoWQqpHSH
        8TEiLtemSsSTVqNBayK7JLILNs6l60N/24nk3PUwvzFdEeIf99ImLjeJaYzQAo5e
        +JIC2RpzbURhBJe3Ib/bC4ie4qjSsL873xYiDzJOc/+tL8XTYAzDMFMaFQKBgQCe
        Qx6ojWEtyYYuITZhdw7ELcJ3erzIyKB8PrphNBtg43HOBLcU68iDeyzYOVFg5WNZ
        dae76Zm/ur8TtVX6FKUpeabuXzskox63OYKfvnyYF+NGZN1hKaanxVqCLxhzmOdU
        9vfDEL3CRVH/r+wIS/z/ZsOCyCGqZR+xIMOXJYugYwKBgQC1yvwsqYMHGgJjiH2s
        dbQ00wWXsreBVmrJwO1RSkq/EUVlfiEy6HmGk2vB7v7qZXfXAUjvCvIJCrYAOBuo
        TN+U6EbnrI4mxv4VGQp5EEKvqr3nTitpFTFIJu/VPG7+z0GIZdlgG+T+DllGPJZ/
        lIuY5FgdCkVxG/Qu3CA/Ux4xwg==
        -----END PRIVATE KEY-----
        """;
}