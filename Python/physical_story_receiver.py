"""Main-app timed capture with automatic local printed-label OCR by default.

No new calibration, spelling correction, or cloud vision API. The legacy manual
adapter remains available for compatibility tests; the main CLI always uses auto.
"""
import argparse
import json
import os
from pathlib import Path
import sys

import camera_frame_server
from camera_frame_server import FrameHTTPServer, FrameStore
from table_roi_adapter import recognize as save_hand_region, validate_region


PROJECT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = PROJECT / "runtime" / "physical-story-captures"
DEFAULT_ARCHIVE_CONFIG = PROJECT / "runtime" / "capture-archive.json"
DEFAULT_SAVED_REFERENCES = PROJECT / "runtime" / "camera-captures" / "color-calibration.json"


def recognize(frame_bgr, metadata):
    """Save the genuine hand-selected crop, then wait for manual brick quads."""
    from lego_color_adapter import recognize as recognize_colors
    region = save_hand_region(frame_bgr, metadata)
    pending = recognize_colors(frame_bgr, metadata)
    pending.update(
        recognitionPerformed=False,
        roiWidth=region["roiWidth"], roiHeight=region["roiHeight"],
        pythonCropMs=region["pythonCropMs"],
        message="Photo and hand region received. Select brick faces on this exact frame, then Recognize colors.",
    )
    return pending


recognize.supports_color_regions = True


def configured_archive_dir(config_path=DEFAULT_ARCHIVE_CONFIG):
    """Persist the user's Mac destination across ordinary receiver restarts."""
    if not config_path.is_file():
        return DEFAULT_OUTPUT / "capturedimage2026"
    value = json.loads(config_path.read_text())["archiveDirectory"]
    if not isinstance(value, str) or not Path(value).is_absolute():
        raise ValueError("capture-archive.json requires an absolute archiveDirectory")
    return Path(value)


def build_store(output_dir, saved_references=None, automatic=False, recognition_mode="ocr", archive_dir=None):
    """Read existing references only; the HTTP server disables calibration."""
    output = Path(output_dir).resolve()
    output.mkdir(parents=True, exist_ok=True)
    os.environ["TABLE_DEPTH_OUTPUT"] = str(output)
    if recognition_mode in ("ocr", "braille"):
        if recognition_mode == "ocr":
            from lego_ocr_adapter import recognize as read_local, PROVENANCE, METHOD
        else:
            from lego_braille_adapter import recognize as read_local, PROVENANCE, METHOD
        def local_recognizer(frame_bgr, metadata, unity_crop_bgr=None):
            result = read_local(frame_bgr, metadata, unity_crop_bgr=unity_crop_bgr)
            try:
                validate_region(frame_bgr, metadata)
            except (ValueError, TypeError):
                return result
            if unity_crop_bgr is None:
                crop = save_hand_region(frame_bgr, metadata)
                result.update(roiWidth=crop["roiWidth"], roiHeight=crop["roiHeight"], pythonCropMs=crop["pythonCropMs"])
            else:
                # FrameStore already saved the exact final PNG before detection.
                # Do not overwrite it with a second crop of the lossy JPEG.
                region = metadata["recognitionRegion"]
                result.update(roiWidth=region["width"], roiHeight=region["height"])
            return result
        local_recognizer.supports_unity_crop = True
        local_recognizer.recognition_method = "ocr" if recognition_mode == "ocr" else "braille_dots"
        local_recognizer.recognition_input = "target_crop"
        local_recognizer.recognition_provenance = dict(PROVENANCE)
        local_recognizer.recognition_description = METHOD
        return FrameStore(output, local_recognizer, archive_dir=archive_dir)
    if recognition_mode != "color":
        raise ValueError("recognition_mode must be ocr, braille or color")
    # Legacy color mode is opt-in and requires an older upstream checkout.
    # The current upstream intentionally removed its color-reader modules.
    from lego_color_adapter import ColorSession
    store = FrameStore(output, recognize, archive_dir=archive_dir)
    references = Path(saved_references).resolve() if saved_references else None
    if references is not None and references.is_file():
        store.color_session = ColorSession(references)
    if automatic:
        from auto_color_regions import recognize as auto_recognize
        def automatic_recognizer(frame_bgr, metadata):
            if metadata.get("trigger", {}).get("gesture") == "double_pinch_timer":
                # A requested timer photo is still a successful capture when the
                # frozen target cannot be projected into this image. Keep the
                # full JPEG, but never classify unrelated pixels as that word.
                try:
                    validate_region(frame_bgr, metadata)
                except (ValueError, TypeError):
                    return {
                        "status": "retry_needed", "word": None, "letters": [], "display": "",
                        "message": "Photo received, but the word region is outside the view or unavailable. Center the whole word and double pinch again.",
                        "automaticBlockDetection": True, "recognitionPerformed": False,
                        "coordinateSystem": "image_pixels_top_left", "cells": [],
                        "method": "timer_capture_without_valid_word_region",
                        "recognitionRegionIssue": str(metadata.get("recognitionRegionIssue") or "Invalid or missing word region")[:240],
                    }
            crop = save_hand_region(frame_bgr, metadata)
            result = auto_recognize(frame_bgr, metadata, store.color_session)
            result.update(roiWidth=crop["roiWidth"], roiHeight=crop["roiHeight"], pythonCropMs=crop["pythonCropMs"],
                          automaticBlockDetection=True)
            return result
        store.recognizer = automatic_recognizer
    return store


def dashboard(saved_references_available):
    # Reuse the tested four-corner selection flow. Coordinates remain in the
    # full image, so /color/analyze and Quest's projection contract are unchanged.
    page = camera_frame_server.DASHBOARD.decode("utf-8")
    page = page.replace("Quest Color Test", "Story Bricks — Physical Word Input")
    page = page.replace(
        "One real camera frame per manual capture. Select brick faces here; Quest displays the recognized letters using the captured camera geometry and your surface reference. No automatic block detector or hands-off trigger is running.",
        "The main Quest app sends a real photo after a tracked hand lift. Select the brick faces below; the original Python color algorithm reads them and returns the result to the story app. Block boundaries are selected manually.")
    page = page.replace(
        "In Quest, set the block-surface reference as instructed in the headset. Press the <b>right trigger</b> to capture.",
        "In the main Quest app, use physical input. Look at the table, approach it with a hand, then lift and move both visible hands clear. The cyan dashed rectangle is the suggested hand region, not a detected word boundary.")
    page = page.replace(
        "Images stay on this Mac; only the latest capture is retained.",
        "Select only the completed word. A fully accepted result can advance the live story conversation; unknown letters do not. Only the latest capture is retained.")
    page = page.replace("Start the color receiver on this Mac.", "Start the physical-story receiver on port 8769.")
    if not saved_references_available:
        page = page.replace('<option value="quest_color_calibration">Saved camera references</option>',
                            '<option value="quest_color_calibration" disabled>Saved camera references unavailable</option>')
    page = page.replace("analysisKey='';", "analysisKey='', suggestedROI=null;")
    page = page.replace("ctx.drawImage(picture,0,0);const scale=", """ctx.drawImage(picture,0,0);
const roi=suggestedROI;if(roi&&[roi.x,roi.y,roi.width,roi.height].every(Number.isInteger)&&roi.x>=0&&roi.y>=0&&roi.width>0&&roi.height>0&&roi.x+roi.width<=canvas.width&&roi.y+roi.height<=canvas.height){ctx.save();ctx.strokeStyle='#64ddeb';ctx.lineWidth=Math.max(2,canvas.width/400);ctx.setLineDash([10,6]);ctx.strokeRect(roi.x,roi.y,roi.width,roi.height);ctx.restore();}const scale=""")
    page = page.replace("picture=img;canvas.width=", "picture=img;suggestedROI=meta.recognitionRegion||null;canvas.width=")
    page = page.replace("if(!state.latest){el('status').textContent=", """if(!state.latest){revision='';active=null;picture=null;suggestedROI=null;cells=[];points=[];canvas.style.display='none';loading=false;controls();el('selection').textContent='No frame yet.';el('metadata').textContent='No frame received.';el('result').textContent='No recognition yet.';el('status').textContent=""")
    return page.encode("utf-8")


def automatic_dashboard(recognition_mode="ocr"):
    """Read-only capture monitor; recognition never waits for a pointer action."""
    page = r'''<!doctype html><html lang="en"><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Story Bricks — Automatic Input</title>
<style>body{margin:0;background:#101820;color:#eaf1f5;font:17px system-ui}main{max-width:1320px;margin:28px auto;padding:0 24px}h1{font-size:28px}h2{font-size:20px;margin-top:0}.panel{padding:18px;margin:14px 0;background:#203543;border-radius:12px}.image-pair{display:grid;grid-template-columns:1fr 1fr;gap:16px}.image-pair .panel{min-width:0;margin:0}.input-panel{box-shadow:inset 0 0 0 1px #64ddeb}canvas{max-width:100%;height:auto;border-radius:8px;display:none;background:#000}p{line-height:1.5;color:#c4d0da}pre{white-space:pre-wrap;font-size:12px;overflow:auto}#word{font-size:32px;font-weight:650;color:#a5f1dc}summary{cursor:pointer;padding:8px 0}@media(max-width:760px){main{padding:0 14px}.image-pair{gap:10px}.image-pair .panel{padding:12px}.image-pair h2{font-size:17px}.image-pair p{font-size:13px;min-height:4.5em}}</style>
<main><h1>Story Bricks</h1><p>Double pinch above the word, then move your hands away. A photo is taken after 3 seconds; color recognition runs on its saved word region.</p>
<div class="panel"><div id="status">Waiting for the Quest camera.</div><div id="word"></div><p id="message">Uncertain input asks for a new photo.</p><div id="timing"></div></div>
<div class="image-pair">
<section class="panel" id="original-panel"><h2>Original photo</h2><p id="original-status">Waiting for a capture.</p><canvas id="original" aria-label="Original photo with yellow capture box"></canvas></section>
<section class="panel input-panel"><h2>Recognition input</h2><p id="crop-status">Waiting for a capture.</p><canvas id="frame" aria-label="Actual saved color crop sent to recognition without a black mask"></canvas></section>
</div>
<p>The cropped image keeps its original colors. Only bricks fully inside the yellow box are read; bricks touching the line are ignored. The original and recognition input are saved even when recognition fails.</p>
<details><summary>Capture details</summary><pre id="metadata"></pre></details></main>
<script>
const activeRecognitionMode=__ACTIVE_MODE_JSON__,activeRecognitionAlgorithmId=__ACTIVE_ALGORITHM_JSON__;
const el=id=>document.getElementById(id),canvas=el('frame'),original=el('original');let revision='',busy=false;
function loadImage(url){return new Promise((resolve,reject)=>{const img=new Image();img.onload=()=>resolve(img);img.onerror=()=>reject(new Error('Image unavailable'));img.src=url;});}
function draw(canvas,img){canvas.width=img.naturalWidth;canvas.height=img.naturalHeight;canvas.getContext('2d').drawImage(img,0,0);canvas.style.display='block';}
function clearImages(){canvas.style.display='none';original.style.display='none';}
async function refresh(){
 if(busy)return;busy=true;
 try{
  const response=await fetch('/latest.json',{cache:'no-store'});if(!response.ok)throw new Error('Receiver unavailable');
  const state=await response.json();
  if(!state.latest){revision='';clearImages();el('status').textContent='Ready — waiting for a new word photo.';el('word').textContent='';el('message').textContent='Double pinch above the word; the photo follows after 3 seconds.';el('timing').textContent='';el('crop-status').textContent='Waiting for a capture.';el('original-status').textContent='Waiting for a capture.';el('metadata').textContent='';return;}
  const latest=state.latest,m=latest.metadata,r=latest.colorAnalysis?.recognition||latest.response.recognition;
  const key=m.sessionId+':'+m.frameId;
  const previousMode=activeRecognitionMode==='ocr'&&r.algorithm_id!==activeRecognitionAlgorithmId;
  el('status').textContent=previousMode?'Previous recognition mode — take a new photo for OCR':'Photo '+state.receivedCount+' · '+r.status;
  el('word').textContent=previousMode?'':r.word||'';
  el('message').textContent=previousMode?'This is a saved result from a previous recognition mode. Take a new photo to read printed labels with OCR.':r.message||'';
  el('timing').textContent=previousMode?'Saved photo shown for reference; current OCR has not run on this capture.':Number.isFinite(r.proposalTimingMs)?'Local recognition: '+r.proposalTimingMs.toFixed(0)+' ms':'';
  el('metadata').textContent=JSON.stringify({metadata:m,recognition:r,crop:latest.response.archive?.crop},null,2);
  if(revision===key)return;
  revision=key;clearImages();el('crop-status').textContent='Loading the saved recognition input…';el('original-status').textContent='Loading the original photo…';
  const query='?sessionId='+encodeURIComponent(m.sessionId)+'&frameId='+encodeURIComponent(m.frameId);
  const crop=latest.response.archive?.crop;
  const cropTask=(async()=>{
   if(crop?.status!=='saved'){el('crop-status').textContent='No valid crop was saved for this capture. The original is available separately.';return;}
   try{
    const img=await loadImage('/latest-crop.png'+query);
    if(img.naturalWidth!==crop.width||img.naturalHeight!==crop.height)throw new Error('Crop dimensions changed');
    draw(canvas,img);
    const masked=crop.captureMask?.applied===true||r.recognitionInput?.captureMask?.applied===true;
    el('crop-status').textContent=img.naturalWidth+' × '+img.naturalHeight+' pixels · '+(masked?'previous masked capture · take a new photo':'color crop · no black mask');
   }catch(error){canvas.style.display='none';revision='';el('crop-status').textContent='Crop unavailable or a newer photo arrived. Retrying…';}
  })();
  const originalTask=(async()=>{
   try{
    const img=await loadImage('/latest.jpg'+query);draw(original,img);
    const roi=m.recognitionRegion,quad=roi?.readingQuad,ctx=original.getContext('2d');
    ctx.strokeStyle='#ffd35c';ctx.lineWidth=Math.max(3,original.width/350);
    if(Array.isArray(quad)&&quad.length===4&&quad.every(p=>Number.isFinite(p.x)&&Number.isFinite(p.y))){ctx.beginPath();quad.forEach((p,i)=>i?ctx.lineTo(p.x,p.y):ctx.moveTo(p.x,p.y));ctx.closePath();ctx.stroke();}
    else if(roi&&[roi.x,roi.y,roi.width,roi.height].every(Number.isInteger)&&roi.x>=0&&roi.y>=0&&roi.width>0&&roi.height>0&&roi.x+roi.width<=original.width&&roi.y+roi.height<=original.height){ctx.strokeRect(roi.x,roi.y,roi.width,roi.height);}
    el('original-status').textContent=img.naturalWidth+' × '+img.naturalHeight+' pixels · yellow outline: capture box';
   }catch(error){original.style.display='none';revision='';el('original-status').textContent='Original unavailable or a newer photo arrived. Retrying…';}
  })();
  await Promise.all([cropTask,originalTask]);
 }catch(error){revision='';clearImages();el('status').textContent='Waiting for the local receiver to reconnect.';el('crop-status').textContent='Waiting for the receiver.';el('original-status').textContent='Waiting for the receiver.';}
 finally{busy=false;}
}
refresh();setInterval(refresh,300);
</script></html>'''
    active_mode = recognition_mode if recognition_mode in ("ocr", "braille", "color") else "unknown"
    page = page.replace("__ACTIVE_MODE_JSON__", json.dumps(active_mode))
    page = page.replace("__ACTIVE_ALGORITHM_JSON__", json.dumps("automatic_ppocrv4_labels_v1" if active_mode == "ocr" else None))
    if recognition_mode == "ocr":
        page = page.replace("color recognition runs on its saved word region.", "local OCR reads printed labels in its saved word region.")
        page = page.replace("Only bricks fully inside the yellow box are read; bricks touching the line are ignored.",
                            "Only printed labels fully inside the yellow box are read; labels touching the line are ignored.")
    elif recognition_mode == "braille":
        page = page.replace("color recognition runs on its saved word region.", "local braille-dot recognition runs on its saved word region.")
    return page.encode("utf-8")


def main():
    if sys.version_info[:2] != (3, 12):
        raise SystemExit("Use the Python/.ocr-venv Python 3.12 recognition environment.")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8769)
    parser.add_argument("--recognition", choices=("ocr", "braille", "color"), default="ocr",
                        help="Local printed-label OCR by default; braille and color are explicit legacy modes")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--archive-dir", type=Path, help="Save every received original photo; defaults to the saved Mac destination or runtime/physical-story-captures/capturedimage2026")
    test_references = DEFAULT_OUTPUT / "quest-test-color-references.json"
    parser.add_argument("--saved-references", type=Path, default=test_references if test_references.is_file() else DEFAULT_SAVED_REFERENCES,
                        help="Read-only approved Quest color references for automatic recognition")
    args = parser.parse_args()
    if args.recognition == "ocr":
        from lego_ocr_adapter import get_engine
        get_engine()  # Load the local models once before accepting a capture.
    archive_dir = args.archive_dir if args.archive_dir is not None else configured_archive_dir()
    store = build_store(args.output_dir, args.saved_references, automatic=True, recognition_mode=args.recognition, archive_dir=archive_dir)
    camera_frame_server.DASHBOARD = automatic_dashboard(args.recognition)
    server = FrameHTTPServer(("127.0.0.1", args.port), store)
    print("Physical story camera receiver: http://127.0.0.1:{}".format(args.port), flush=True)
    print("Original photos are permanently saved on this Mac: " + str(store.archive_dir), flush=True)
    print("Valid target crops are saved losslessly before recognition: " + str(store.archive_dir / "crops"), flush=True)
    if args.recognition == "ocr":
        print("Input: timed native color crops; local printed-label OCR. " + store.recognizer.recognition_description, flush=True)
        print("Recognition provenance: " + json.dumps(store.recognizer.recognition_provenance, sort_keys=True), flush=True)
        print("Printed labels inside the captured box supply letters A-Z and periods. Uncertain labels request another capture.", flush=True)
    elif args.recognition == "braille":
        print("Input: timed frames; automatic grayscale face localization + unchanged LEGO_OCR.BrailleReader. No color fallback.", flush=True)
        print("Recognition provenance: " + json.dumps(store.recognizer.recognition_provenance, sort_keys=True), flush=True)
        print("Uncertain geometry, orientation or dots request another capture. Invented words are allowed; no word dictionary filter.", flush=True)
    else:
        print("LEGACY COLOR TEST: automatic color regions + original ColorReader; references=" + store.color_session.source, flush=True)
    print("Calibration, spelling correction, and cloud vision calls: disabled.", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
