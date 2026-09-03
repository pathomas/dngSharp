# DNG SDK — Architecture Overview

This diagram shows the major components of the DNG SDK and how they relate to one another. The flow runs from raw file input at the top through parsing, the core pipeline, rendering, and finally to output.

```mermaid
flowchart TD
    STREAM["📄 dng_stream\nfile / memory byte source"]

    subgraph HOST["dng_host — application control point"]
        direction LR
        H1["memory allocator\n& abort sniffer"]
        H2["metadata / image\nneeds flags"]
        H3["JXL encode settings\n& color-space info"]
        H4["object factories\n(negative, image, opcode, …)"]
    end

    subgraph PARSE["Parse layer"]
        INFO["dng_info\nTIFF/IFD container parsing\n─────────────────\nreads IFDs, discovers\nmain / mask / depth /\nenhanced / semantic indices\nfills dng_shared & dng_exif"]
    end

    subgraph NEGATIVE["dng_negative — central owner"]
        direction TB
        subgraph META["Metadata"]
            EXIF["dng_metadata\n(EXIF / XMP / IPTC)"]
            CAM["dng_camera_profile []\ncalibration matrices\nforward matrices\ntone curve · HSV map\ndynamic-range flag"]
        end
        subgraph PIPELINE["Image pipeline"]
            LIN["dng_linearization_info\nLinearizationTable\nBlackLevel · WhiteLevel"]
            MOS["dng_mosaic_info\nCFA pattern · Bayer layout"]
            S1["Stage 1 image\nraw sensor / enhanced data"]
            S2["Stage 2 image\nlinearized  [0 … 1]"]
            S3["Stage 3 image\ndemosaiced  [0 … 1]"]
            OP1["OpcodeList1\napplied on raw values"]
            OP2["OpcodeList2\napplied on linear values"]
            OP3["OpcodeList3\napplied on demosaiced values"]
        end
        S1 -->|"OpcodeList1 →\nlinearize (black sub,\nwhite rescale)"| S2
        S2 -->|"OpcodeList2 →\ndemosaic CFA"| S3
        S3 -->|OpcodeList3| S3OUT((" "))
    end

    subgraph RENDER["Render layer"]
        REND["dng_render\ncamera → XYZ (D50) → ProPhoto RGB\nProfileGainTableMap2\nHSV · tone curve · look table\nHDR encode/decode (if HDR profile)"]
        RIMG["rendered dng_image"]
    end

    subgraph OUTPUT["Output layer"]
        WRITER["dng_image_writer\nwrites TIFF / DNG / JPEG previews\nembed XMP · preview list\nJXL tile encoding"]
    end

    subgraph VENDORED["Vendored libraries"]
        direction LR
        JPEG["libjpeg\nJPEG codec\n(lossless Huffman +\nbaseline DCT)"]
        JXL["libjxl\nJPEG XL codec\n(encode + decode,\nbrotli + highway deps)"]
        XMPSDK["Adobe XMP SDK\nXMP parse / serialize\n(expat + zlib deps)"]
    end

    %% top-level data flow
    STREAM -->|"Parse() / PostParse()"| INFO
    INFO -->|"parsed IFDs\n& shared state"| NEGATIVE
    HOST -->|"creates & configures"| NEGATIVE
    HOST -->|"creates"| INFO

    %% render
    NEGATIVE -->|"stage 3 image\n+ camera profiles"| REND
    HOST -->|"render settings\n(white point, exposure,\ncolor space, max size)"| REND
    REND --> RIMG

    %% output
    NEGATIVE -->|"metadata, opcodes\nprofiles, gain maps"| WRITER
    RIMG -->|"rendered pixels"| WRITER
    HOST -->|"save flags\nDNG version"| WRITER

    %% vendored lib connections
    WRITER <-->|"JPEG tile I/O\npreview encode"| JPEG
    WRITER <-->|"JXL tile I/O"| JXL
    EXIF <-->|"XMP parse/serialize"| XMPSDK
    WRITER -->|"embed XMP block"| XMPSDK
```

## Component responsibilities

| Component | Responsibility |
|---|---|
| `dng_stream` | Byte-level I/O abstraction over file or memory; all reads/writes go through this |
| `dng_host` | Application control point: memory allocation, abort/progress callbacks, JXL encode settings, object factories for every major type |
| `dng_info` | TIFF/IFD-level parser; discovers main, mask, depth, enhanced, and semantic-mask IFD indices; fills `dng_shared` and `dng_exif` structs |
| `dng_negative` | Central owner of the entire image and metadata state; drives the stage pipeline; owns opcode lists, camera profiles, and all three stage images |
| `dng_linearization_info` | Stores `LinearizationTable`, black-level grid, and white-level used during Stage 1 → Stage 2 conversion |
| `dng_mosaic_info` | CFA / Bayer pattern description; drives demosaic during Stage 2 → Stage 3 |
| `dng_camera_profile` | Per-profile calibration: color matrices, forward matrices, HSV/look tables, tone curve, dynamic-range flag; a `dng_negative` holds a vector of these |
| `dng_render` | Color pipeline: camera-space → XYZ (D50) → ProPhoto RGB, applies `ProfileGainTableMap2`, HSV table, tone curve, and look table; produces a rendered `dng_image` |
| `dng_image_writer` | Serializes a `dng_negative` or `dng_image` to TIFF or DNG; handles JXL tile encoding, JPEG preview encoding, XMP embedding, and preview list |
| **libjpeg** | JPEG codec used for lossless-Huffman raw tiles and baseline-DCT rendered previews |
| **libjxl** | JPEG XL codec (+ brotli + highway SIMD deps) used for JXL-compressed raw/enhanced tiles (DNG ≥ 1.7.0) |
| **Adobe XMP SDK** | XMP metadata parse and serialize (+ expat XML parser + zlib) |

## Stage pipeline

```
Raw file
  └─ Stage 1  raw sensor values (as stored in the file, possibly JXL/JPEG-compressed)
       │  OpcodeList1  (applied to raw integer/float values)
       │  LinearizationTable LUT (optional)
       │  Black-level subtraction  (BlackLevel + delta grids)
       │  White-level rescale  → [0.0, 1.0]
       ▼
     Stage 2  linearized scene-referred values
       │  OpcodeList2  (warp, gain, bad-pixel correction on linear values)
       │  Demosaic CFA  (if Bayer/X-Trans mosaic input)
       ▼
     Stage 3  demosaiced linear values
       │  OpcodeList3  (applied after demosaic)
       ▼
     dng_render  camera → XYZ → ProPhoto → tone/HSV/look → output color space
       ▼
     Rendered image  (ready to write as TIFF or embed in output DNG)
```
