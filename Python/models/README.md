# Bundled English OCR model

`en_PP-OCRv3_rec_infer.onnx` is the English PP-OCRv3 recognition model, converted from PaddleOCR and published by **SWHL / RapidAI (RapidOCR)**. It is included unchanged as an ordinary Git binary so team setup and runtime do not need another model download. It is not a Git LFS pointer.

- Size: **8,967,018 bytes** (8.97 MB).
- SHA256: `ef7abd8bd3629ae57ea2c28b425c1bd258a871b93fd2fe7c433946ade9b5d9ea`.
- [Pinned model source](https://huggingface.co/spaces/RapidAI/RapidOCR/resolve/ad05aa73dc29dbf3d2a4f3d8af303244cdf91634/models/text_rec/en_PP-OCRv3_rec_infer.onnx).
- [Publisher model card](https://huggingface.co/SWHL/RapidOCR), which identifies the PaddleOCR conversion and Apache-2.0 license.
- [Original PaddleOCR training configuration](https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.6/configs/rec/PP-OCRv3/en_PP-OCRv3_rec.yml).

The model works with the existing `rapidocr-onnxruntime==1.4.4` and `onnxruntime==1.20.1` integration. The parallel OCR adapter uses it only for one row retry after the existing primary OCR path fails; successful primary reads stay unchanged. It selects this file through `rec_model_path` with `rec_img_shape=[3, 48, 320]`; the runtime reads its character dictionary from the ONNX metadata. This is a general English-character model, not a list of permitted demo words. The integration retains the 0.85 acceptance threshold and its geometry, completeness and period checks. The original LEGO_OCR source remains separate and unchanged.

The weights are licensed under **Apache License 2.0**. Retained upstream notices and license texts are in [LICENSE-RapidOCR.txt](LICENSE-RapidOCR.txt) and [LICENSE-PaddleOCR.txt](LICENSE-PaddleOCR.txt):

- Copyright (c) 2021 RapidOCR Authors. All rights reserved.
- Copyright (c) 2016 PaddlePaddle Authors. All Rights Reserved.

[en_PP-OCRv3_rec_infer.source.json](en_PP-OCRv3_rec_infer.source.json) records the source revision, URL, checksum, license sources and unchanged-file status. Preserve these files when redistributing the model.
