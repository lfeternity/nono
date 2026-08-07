# Clipboard OCR Models

The clipboard OCR pipeline uses the official PP-OCRv5 Server detection and recognition inference models, plus the English PP-OCRv5 Mobile recognition model for technical-text verification.

- Source: `https://github.com/PaddlePaddle/PaddleOCR`
- License: Apache License 2.0
- Detection archive: `PP-OCRv5_server_det_infer.tar`
- Recognition archive: `PP-OCRv5_server_rec_infer.tar`
- English recognition archive: `en_PP-OCRv5_mobile_rec_infer.tar`
- Downloaded: 2026-08-07

Archive SHA-256:

- Detection: `22A33E0BA6A21425EA4192DA03BF4395C9A0C67902BD924B7328FC859073045D`
- Recognition: `D99BE2FFD348943AB52876179168BE4FB5B14F5F0812F2AE4C76D89EC2EA750A`
- English recognition: `E595B4CF2FFAD19FBB5A61BA345D63939577A3AB8717B6E5995642590C9101B4`

The `ppocrv5_server_dict.txt` and `en_ppocrv5_dict.txt` files are generated without a BOM from each official recognition model's `PostProcess.character_dict` list in `inference.yml`.
