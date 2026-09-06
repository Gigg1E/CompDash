# CompDash

A WPF dashboard over the media tools in `C:\Tools`. Same ffmpeg underneath, but every
setting the old scripts hard-coded is a control you can move, and each section shows what
the output would weigh with the settings up to that point applied.

Three tabs: **Studio** converts and compresses one file at a time, **PDF** merges a pile of
mixed files into a single document in an order you choose, and **Toolbox** launches the
remaining `C:\Tools` scripts.

## Requirements

- .NET 10 SDK (built and tested against 10.0.400)
- ffmpeg and ffprobe on `PATH` — `winget install Gyan.FFmpeg` if you need them

Optional, only for the PDF tab:

- Microsoft Edge, for HTML and SVG (ships with Windows)
- LibreOffice, for Word / Excel / PowerPoint — `winget install TheDocumentFoundation.LibreOffice`

The badge top-right turns red if it cannot find ffmpeg; hover it for the path it did find.
As well as `PATH`, it looks in the winget links folder, `C:\ffmpeg\bin`,
`C:\Program Files\ffmpeg\bin` and `C:\Tools`.

## Building and running

```
dotnet publish -c Release -o dist
```

then

```
CompDash.bat
CompDash.bat "C:\clips\demo.mp4"      also accepts several files
```

or double-click `dist\CompDash.exe`. Build output is not committed; run the publish once
after cloning.

## Studio tab

Drop files anywhere on the window, or use **Add files**. Everything queued shares one set
of settings — **Convert** does the selected file, **All files** does the whole queue.

**Convert to** picks the output container. The rest of the panel changes with it: GIF loses
the codec controls and gains the palette, images lose trim and frame rate, WebM quietly
moves you to VP9/Opus because it cannot hold H.264 (it tells you when it does).

The sections, each with a running size estimate on its right:

| | |
|---|---|
| **1 · Source & trim** | Start/end, so you can cut before compressing. |
| **2 · Frame size** | Original, percent, fit-a-width, or exact. Scaler choice included — `neighbor` for pixel art, `lanczos` otherwise. |
| **3 · Frame rate** | Only drops frames; it will not invent them. |
| **4 · Colour & palette** | For GIF (and PNG, if you tick *Quantise*): colour count 2–256, dither algorithm, and whether the palette is rebuilt per change or fixed for the whole clip. For video: greyscale, saturation, contrast, brightness, and the pixel format. |
| **5 · Compression & codec** | H.264 / H.265 / VP9 / AV1, and three ways to ask for a size: constant quality (CRF), a fixed bitrate, or a target file size. For images this becomes the JPEG/WebP quality or PNG deflate level. |
| **6 · Audio** | Copy, re-encode, or drop it. |
| **7 · Output & command** | Where it lands, the filename suffix, and the exact ffmpeg command — copyable, so you can check the tool's work or reuse it in a script. |

**Target file size** is `squeeze.ps1` generalised: it solves the bitrate for your number,
encodes two-pass, and if the result still overshoots it drops the bitrate and goes again,
up to four times. Unlike the script it does not have to leave the resolution alone.

### The estimates

Two different things, deliberately labelled differently.

The **big number** is measured. For anything under six seconds, and for every still image,
it encodes the whole file with your exact settings and reads the size off disk — that is
the answer, not a guess. For longer clips it encodes three short slices from across the
timeline and extrapolates; on a 40-second test that landed within 2–5% of the real output.
The line underneath tells you which of the two you are looking at.

The **per-section numbers** are modelled — instant, so they keep up while you drag a
slider. When the measurement finishes, the difference between the model and reality is
spread back across the sections in proportion to how much each one claims to be doing, so
the chain always ends on the measured number and a section that changes nothing stays
showing no change. Treat them as "which knob is paying for itself", not as promises.

The model alone can be 50% out on unusual footage. That is why it is not the headline.

### The preview

Left is an untouched frame. Right is a frame that has been through the encoder with your
current settings — a real 0.7-second sample is encoded and a frame pulled back out — so
CRF blockiness and palette banding actually show up instead of being invisible until you
commit. The **At** slider moves both. Untick **auto** if the constant re-encoding gets in
the way on a big file.

## PDF tab

Merge a pile of mixed files into one PDF, in an order you control.

Add files or a whole folder, or drop them on the window while this tab is open. They arrive
in **natural** name order, so `page2` lands before `page10` instead of after it.

Four ways to set the order, and they all agree with each other:

- **Type a number** into the box on the left of a row and press Enter — the row jumps to that
  position and everything renumbers.
- **Drag a row** up or down.
- **▲ ▼** on the row, or the Move up / Move down buttons.
- **Sort A→Z**, **Sort by date**, **Reverse**.

Untick a row to leave it out without removing it. For a PDF source, the small box on the
right takes a page range — `1-3,7` or `8-` or even `5-2` to reverse those pages. Blank means
all of them.

### What it can take in

| | |
|---|---|
| Images | PNG, JPEG, BMP, GIF, TIFF, WebP — one page each |
| PDF | merged as-is, with an optional page range |
| Text and code | TXT, MD, CSV, JSON, XML, LOG, PS1, CS, PY… paginated in monospace with the filename as a header |
| HTML, SVG | rendered by headless Edge |
| Word, Excel, PowerPoint, OpenDocument | needs LibreOffice — `winget install TheDocumentFoundation.LibreOffice` |

The **Converters found** panel on the right tells you which of these are live on this
machine, and any row it cannot handle says so in its status instead of failing at build
time. If a file turns out to be broken mid-build it is skipped and named in the log; the
rest of the document still gets written.

### Page setup

Paper (A4 through Tabloid, or **Match each image** to make every page exactly the size of
its picture), rotation, scaling (fit inside / fill and crop / actual size) and margin. With
rotation on **Auto**, a wide image gets a landscape page and a tall one gets portrait.

**Bookmark each source file** adds a PDF outline entry per input, which makes a 200-page
merge navigable. **Number the pages** stamps a footer across the whole document, including
the pages that came from imported PDFs.

The footer under the list keeps a running "N files · about M pages" so you know what you are
about to get. Page counts are exact for PDFs and calculated from the real layout for text.

## Toolbox tab

`disk-analyzer.ps1` and `TREAI.ps1` with their arguments as fields, plus install/remove for
the Explorer right-click menu (that one prompts for elevation, since it writes to
`HKEY_CLASSES_ROOT`). The right-hand panel lists which fixed setting in which old script
became which control here.

The `.bat` files in `C:\Tools` are untouched and still work. For a one-off conversion the
right-click menu is still faster; CompDash is for when you want to see what a setting costs
before you commit to it.

## Presets

The left column carries the old scripts' exact settings as one-click starting points —
*Compress (CRF 28)*, *Squeeze to 500 KB*, *Convert to GIF*, and so on — plus a few new ones
(*Web 720p*, *Fit 10 MB*, *Shrink PNG*). A preset only fills in the controls; change
anything afterwards.

## Checking it still works

```
dist\CompDash.exe --selftest "C:\some\clip.mp4"
```

Drives every output format through the real UI code and writes
`%TEMP%\CompDash\selftest.txt`. Should say `SELFTEST OK` and then list the ffmpeg command
it built for each format. Pass mergeable files too and it also exercises the PDF tab —
adding, renumbering, sorting and a real build:

```
dist\CompDash.exe --selftest clip.mp4 a.png b.png notes.txt report.pdf
```

## Known limits

- One settings set for the whole Studio queue; there is no per-file override.
- The PDF tab applies one page setup to the whole document; you cannot mix A4 and Letter in
  a single merge.
- Merging is done in memory, so a merge of several hundred megabytes of source PDFs will
  use a matching amount of RAM.
- GIF has no target-size mode — its size comes from frame size, frame rate and palette, so
  those are the knobs.
- Long-clip estimates assume the three sampled slices are representative. A clip that is
  static for a minute and then all motion will estimate low.
