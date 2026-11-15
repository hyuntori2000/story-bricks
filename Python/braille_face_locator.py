"""Conservative grayscale geometry proposals for separated 2x4 braille top faces.

No color prototypes, printed-letter OCR, supplied corners, or saved-photo layouts.
This is a local baseline, not a guarantee that every physical brick was visible.
Both label-end orientations are returned for the dot adapter to resolve or reject.
"""
from __future__ import annotations
from time import perf_counter
import cv2
import numpy as np

MIN_FACE_WIDTH = 28.0
MAX_FACES = 32


def capture_selection_quad(region, origin=(0, 0)):
    """Validate the visible box and express it in the detector's crop pixels."""
    if not isinstance(region, dict):
        return None
    points = region.get("readingQuad")
    explicit = not (region.get("readingOrientation") in (None, "") and points in (None, []))
    if not explicit:
        return None
    if region.get("readingOrientation") != "labels_toward_viewer" or not isinstance(points, list) or len(points) != 4:
        raise ValueError("The capture box requires four ordered reading corners")
    if any(not isinstance(p, dict) or any(type(p.get(k)) not in (int, float) for k in ("x", "y")) for p in points):
        raise ValueError("Capture-box corners must be numeric")
    quad = np.float32([[p["x"], p["y"]] for p in points]) - np.float32(origin)
    if not np.isfinite(quad).all() or not cv2.isContourConvex(quad) or cv2.contourArea(quad, oriented=True) <= 1:
        raise ValueError("The capture box must be finite, clockwise and nondegenerate")
    return quad


def capture_face_membership(face, capture_quad):
    """A full convex face is selected only when all four corners are inside."""
    if capture_quad is None:
        return "inside"
    face = np.asarray(face, np.float32)
    if face.shape != (4, 2) or not np.isfinite(face).all() or not cv2.isContourConvex(face):
        raise ValueError("Invalid face geometry for capture-box selection")
    # A two-pixel geometric guard excludes line-touching faces. It never
    # changes image pixels and is not the old twenty-pixel search padding.
    if all(cv2.pointPolygonTest(capture_quad, (float(p[0]), float(p[1])), True) > 2 for p in face):
        return "inside"
    overlap = float(cv2.intersectConvexConvex(face, capture_quad)[0])
    return "outside" if overlap <= 1e-3 else "crossing"


def _segment_intersects_capture(a, b, capture_quad):
    """Clip evidence to the convex target without changing any source pixels."""
    if capture_quad is None:
        return True
    start, end = 0.0, 1.0
    for corner, following in zip(capture_quad, np.roll(capture_quad, -1, axis=0)):
        edge = following - corner
        da, db = a - corner, b - corner
        first = float(edge[0] * da[1] - edge[1] * da[0])
        delta = float(edge[0] * (db[1] - da[1]) - edge[1] * (db[0] - da[0]))
        if abs(delta) < 1e-9:
            if first < -1e-3:
                return False
            continue
        boundary = (-1e-3 - first) / delta
        if delta > 0:
            start = max(start, boundary)
        else:
            end = min(end, boundary)
        if start > end:
            return False
    return True


def _select_capture_faces(proposals, capture_quad, counts, ignored=None):
    selected = []
    for quad, quality in proposals:
        membership = capture_face_membership(quad, capture_quad)
        counts[membership] += 1
        if membership == "inside":
            selected.append((quad, quality))
        elif membership == "crossing" and ignored is not None:
            ignored.append(quad)
    return selected


def _boundary_connected_fragment(a, b, capture_quad, distance, maximum_width):
    """Recognize a partial face edge joined to the capture boundary at both ends.

    This only suppresses measured boundary-adjacent fragments; unexplained
    edges wholly inside the box still trigger the existing completeness check.
    """
    if capture_quad is None:
        return False
    vector = b - a
    length = float(np.linalg.norm(vector))
    if length < 1:
        return False
    normal = np.float32([-vector[1], vector[0]]) / length
    for direction in (normal, -normal):
        intersections = []
        for point in (a, b):
            hits = []
            for index, (corner, following) in enumerate(zip(capture_quad, np.roll(capture_quad, -1, axis=0))):
                edge = following - corner
                try:
                    along, fraction = np.linalg.solve(np.column_stack((direction, -edge)), corner - point)
                except np.linalg.LinAlgError:
                    continue
                if 0 <= along <= maximum_width and 0 <= fraction <= 1:
                    hits.append((float(along), point + along * direction, index))
            if not hits:
                break
            intersections.append(min(hits, key=lambda hit: hit[0]))
        if len(intersections) != 2 or intersections[0][2] != intersections[1][2]:
            continue
        # LSD stops slightly inside a corner. Match the already-used two-pixel
        # edge-fit allowance instead of interpreting that gap as a missing cap.
        axis = vector / length
        if all(max(_support(distance, point + offset * axis, hit[1] + offset * axis)
                   for offset in (-2, -1, 0, 1, 2)) >= .54 for point, hit in zip((a, b), intersections)):
            return True
    return False


def _retry(reason, message, cells=None, **diagnostics):
    return {"status": "retry_needed", "reason": reason, "message": message,
            "cells": cells or [], "method": "grayscale_edge_face_geometry",
            "completenessVerified": False, "diagnostics": diagnostics}


def _ordered(q):
    q = np.asarray(q, np.float32).reshape(4, 2)
    center = q.mean(axis=0)
    q = q[np.argsort(np.arctan2(q[:, 1]-center[1], q[:, 0]-center[0]))]
    if cv2.contourArea(q, oriented=True) < 0:
        q = q[::-1]
    lengths = np.linalg.norm(np.roll(q, -1, axis=0)-q, axis=1)
    starts = [int(np.argmin(lengths))]
    starts.append((starts[0]+2) % 4)
    # Of the two possible short first edges, choose the visually upper end.
    first = min(starts, key=lambda i: (float((q[i]+q[(i+1)%4])[1]), float((q[i]+q[(i+1)%4])[0])))
    return np.roll(q, -first, axis=0)


def _support(distance, a, b):
    count = max(16, int(np.linalg.norm(b-a)))
    points = np.linspace(a, b, count)[2:-2]
    xy = np.rint(points).astype(int)
    good = (xy[:, 0]>=0)&(xy[:, 0]<distance.shape[1])&(xy[:, 1]>=0)&(xy[:, 1]<distance.shape[0])
    if good.mean() < .98:
        return 0.0
    return float((distance[xy[good, 1], xy[good, 0]] <= 1.8).mean())


def _quality(q, distance):
    if not cv2.isContourConvex(q) or cv2.contourArea(q) < 1000:
        return None
    edge = np.roll(q,-1,axis=0)-q
    sizes = np.linalg.norm(edge,axis=1)
    width = float(min(sizes[0],sizes[2])); length = float((sizes[1]+sizes[3])*.5)
    if width < MIN_FACE_WIDTH or not 1.6 <= length/((sizes[0]+sizes[2])*.5) <= 2.6:
        return None
    if min(sizes[0],sizes[2])/max(sizes[0],sizes[2]) < .68 or min(sizes[1],sizes[3])/max(sizes[1],sizes[3]) < .68:
        return None
    # Reject strongly skewed contours that are commonly background fragments.
    angles = [abs(float(np.dot(edge[i],edge[(i+1)%4])/(sizes[i]*sizes[(i+1)%4]))) for i in range(4)]
    if max(angles) > .55:
        return None
    supports = [_support(distance,q[i],q[(i+1)%4]) for i in range(4)]
    if min(supports) < .54 or np.mean(supports) < .76:
        return None
    return {"edgeSupport": float(np.mean(supports)), "minimumEdgeSupport": min(supports),
            "estimatedStudDiameterPx": .3*width, "widthPx": width, "lengthPx": length}


def _line_quads(gray, distance, capture_quad=None):
    lines = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(gray)[0]
    if lines is None:
        return []
    segments=[]
    for line in lines[:,0]:
        a,b=line[:2],line[2:];v=b-a;size=float(np.linalg.norm(v))
        if capture_quad is not None and any(cv2.pointPolygonTest(capture_quad,(float(p[0]),float(p[1])),True)<=3 for p in (a,b)):
            continue
        if size < MIN_FACE_WIDTH*1.6:
            continue
        d=v/size
        if d[1]<0 or (abs(d[1])<.001 and d[0]<0):a,b=b,a;d=-d
        segments.append((a,b,d,size))
    # Keep work bounded in textured scenes and fail closed if overloaded.
    if len(segments)>400:
        return None
    proposals=[]
    for i,(a,b,d,length) in enumerate(segments):
        normal=np.float32([d[1],-d[0]])
        for c,e,other,other_length in segments[i+1:]:
            if float(np.dot(d,other)) < .985:
                continue
            width=abs(float(np.dot(c-a,normal)))
            if width<MIN_FACE_WIDTH or width>min(gray.shape)*.65:
                continue
            if not 1.4<=max(length,other_length)/width<=2.7:
                continue
            ta,tb=float(np.dot(a,d)),float(np.dot(b,d))
            tc,te=float(np.dot(c,d)),float(np.dot(e,d))
            if min(tb,te)-max(ta,tc)<.70*max(length,other_length):
                continue
            if abs(ta-tc)>.35*width or abs(tb-te)>.35*width:
                continue
            def point(p,u,t):
                return p+u*((t-float(np.dot(p,d)))/float(np.dot(u,d)))
            first=min(ta,tc);last=max(tb,te)
            # End boundaries must be supported across the face, not inferred from dots.
            radius=min(12,max(5,width*.18))
            ends=[]
            for base in [first,last]:
                options=[]
                for t in np.arange(base-radius,base+radius+1,1):
                    x,y=point(a,d,t),point(c,other,t)
                    options.append((_support(distance,x,y),-abs(t-base),t))
                ends.append(max(options)[2])
            q=_ordered([point(a,d,ends[0]),point(c,other,ends[0]),point(c,other,ends[1]),point(a,d,ends[1])])
            if _quality(q,distance):proposals.append(q)
    return proposals


def _touching_row_quads(gray, distance):
    """Partition a visible row envelope only at measured, continuous seams.

    Intensity levels propose geometry in either polarity. They never identify
    letters or brick colors. Full-height seam evidence distinguishes touching
    faces from an unmarked slab and from the short edges of studs or printing.
    """
    proposals, envelopes, clipped_envelopes = [], [], []
    for level in range(32, 225, 16):
        for polarity in (cv2.THRESH_BINARY, cv2.THRESH_BINARY_INV):
            mask = cv2.threshold(gray, level, 255, polarity)[1]
            for contour in cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)[0]:
                area = cv2.contourArea(contour)
                if area < 2 * 1000:
                    continue
                hull = cv2.convexHull(contour)
                if area / max(cv2.contourArea(hull), 1) < .9:
                    continue
                q = cv2.approxPolyDP(hull, .025 * cv2.arcLength(hull, True), True)
                if len(q) != 4:
                    continue
                q = _ordered(q)
                edge = np.roll(q, -1, axis=0) - q
                horizontal = [i for i in range(4) if abs(edge[i, 0]) > abs(edge[i, 1])]
                if len(horizontal) != 2:
                    continue
                first = min(horizontal, key=lambda i: float((q[i] + q[(i+1) % 4])[1]))
                q = np.roll(q, -first, axis=0)
                # Threshold contours lie slightly inside a blurred boundary.
                # Fit each outer edge to nearby measured edges, at most 2px.
                fitted = []
                for a, b in zip(q, np.roll(q, -1, axis=0)):
                    direction = b-a
                    normal = np.float32([-direction[1], direction[0]]) / np.linalg.norm(direction)
                    offset = max(range(-2,3), key=lambda n: (_support(distance,a+n*normal,b+n*normal),-abs(n)))
                    fitted.append((a+offset*normal,direction))
                corners = []
                for index, (a, direction) in enumerate(fitted):
                    previous, other = fitted[index-1]
                    try:
                        t = np.linalg.solve(np.column_stack((direction,-other)), previous-a)[0]
                    except np.linalg.LinAlgError:
                        break
                    corners.append(a+t*direction)
                if len(corners) != 4:
                    continue
                q = np.float32(corners)
                sizes = np.linalg.norm(np.roll(q, -1, axis=0) - q, axis=1)
                width, height = float((sizes[0]+sizes[2])*.5), float((sizes[1]+sizes[3])*.5)
                count = int(round(2 * width / max(height, 1)))
                if not 2 <= count <= MAX_FACES or height < MIN_FACE_WIDTH * 1.6:
                    continue
                if width / count < MIN_FACE_WIDTH or not 1.6 <= height / (width/count) <= 2.6:
                    continue
                if min(sizes[0], sizes[2]) / max(sizes[0], sizes[2]) < .68 or min(sizes[1], sizes[3]) / max(sizes[1], sizes[3]) < .68:
                    continue
                supports = [_support(distance, q[i], q[(i+1) % 4]) for i in range(4)]
                near_border = (q[:,0]<4).any() or (q[:,0]>gray.shape[1]-5).any() or (q[:,1]<4).any() or (q[:,1]>gray.shape[0]-5).any()
                if near_border and sum(s >= .54 for s in supports) >= 3:
                    clipped_envelopes.append(q)
                    continue
                if min(supports) < .54 or np.mean(supports) < .76:
                    continue
                # A row-shaped boundary with no reliable seams is not a single
                # sideways face. Keep it for overlap rejection below.
                envelopes.append(q)
                cw = int(round(160 * width / height))
                destination = np.float32([[0,0],[cw-1,0],[cw-1,159],[0,159]])
                transform = cv2.getPerspectiveTransform(q, destination)
                rectified = cv2.warpPerspective(gray, transform, (cw,160), flags=cv2.INTER_LINEAR).astype(np.float32)
                # A narrow dark seam has lighter material on both sides. Require
                # its evidence through most of the face, not only label strokes.
                contrast = (rectified[:, :-8] + rectified[:, 8:])*.5 - rectified[:, 4:-4]
                coverage = (contrast[12:145] > 2).mean(axis=0)
                lower = (contrast[12:145] > 4).mean(axis=0)
                boundaries, strengths = [0.0], []
                pitch = (cw-1) / count
                for index in range(1, count):
                    expected = index * pitch
                    candidates = range(max(5, int(expected-.12*pitch)), min(cw-5, int(expected+.12*pitch)+1))
                    best = max(candidates, key=lambda x: (coverage[x-4], lower[x-4], -abs(x-expected)), default=None)
                    if best is None or coverage[best-4] < .65 or lower[best-4] < .35:
                        break
                    boundaries.append(float(best))
                    strengths.append(float(coverage[best-4]))
                if len(boundaries) != count:
                    continue
                boundaries.append(float(cw-1))
                # Extra continuous seams invalidate an inferred brick count.
                strong = np.flatnonzero((coverage >= .65) & (lower >= .35)) + 4
                if any(min(abs(float(x)-b) for b in boundaries) > .15*pitch for x in strong):
                    continue
                inverse = np.linalg.inv(transform)
                row = []
                for left, right in zip(boundaries, boundaries[1:]):
                    if not 1.6 <= 159 / (right-left) <= 2.6:
                        break
                    cell = cv2.perspectiveTransform(np.float32([[[left,0],[right,0],[right,159],[left,159]]]), inverse)[0]
                    cell_sizes = np.linalg.norm(np.roll(cell,-1,axis=0)-cell,axis=1)
                    minimum_width = float(min(cell_sizes[0],cell_sizes[2]))
                    if minimum_width < MIN_FACE_WIDTH:
                        break
                    row.append((cell, {"edgeSupport": float(np.mean(supports+strengths)),
                        "minimumEdgeSupport": float(min(supports+strengths)),
                        "estimatedStudDiameterPx": .3*minimum_width, "widthPx": minimum_width,
                        "lengthPx": float((cell_sizes[1]+cell_sizes[3])*.5),
                        "geometrySource": "touching_row_visible_seams", "rowFaceCount": count,
                        "rowEnvelope": q.tolist(),
                        "minimumSeamCoverage": min(strengths)}))
                if len(row) == count:
                    proposals.extend(row)
    return proposals, envelopes, clipped_envelopes


def _is_clipped_continuation(envelope, faces, distance):
    """A background region enclosing a word is not evidence of a cut-off row."""
    first = faces[0][0]
    across = first[1]-first[0]; across /= np.linalg.norm(across)
    down = np.float32([-across[1], across[0]])
    points = np.concatenate([q for q,_ in faces])
    row_u, row_v = points @ across, points @ down
    env_u, env_v = envelope @ across, envelope @ down
    depth = float(np.ptp(row_v))
    if not .7 <= np.ptp(env_v)/max(depth,1) <= 1.4:
        return False
    if abs(float(env_v.mean()-row_v.mean())) > .25*depth:
        return False
    env_axis = (envelope[1]-envelope[0]) + (envelope[2]-envelope[3])
    env_axis /= np.linalg.norm(env_axis)
    if abs(float(np.dot(env_axis,across))) < .96:
        return False
    width = float(np.median([quality["widthPx"] for _,quality in faces]))
    if max(0,min(float(env_u.max()),float(row_u.max()))-max(float(env_u.min()),float(row_u.min()))) < width*.5:
        return False
    # Inspect only the newly claimed continuation, at both long row edges.
    for left,right in ((float(env_u.min()),float(row_u.min())),(float(row_u.max()),float(env_u.max()))):
        if right-left < .3*width:
            continue
        supports=[]
        for a,b in ((envelope[0],envelope[1]),(envelope[3],envelope[2])):
            direction=b-a;denominator=float(np.dot(direction,across))
            if abs(denominator)<1:
                return False
            p=a+direction*((left-float(np.dot(a,across)))/denominator)
            q=a+direction*((right-float(np.dot(a,across)))/denominator)
            supports.append(_support(distance,p,q))
        if min(supports)>=.54:
            return True
    return False


def _reading_plane(quad, region):
    """Measure a local image quad on the supplied capture plane, in meters."""
    if not isinstance(region, dict) or region.get("readingOrientation") != "labels_toward_viewer":
        return None
    try:
        width, depth = region["physicalWidthMeters"], region["physicalDepthMeters"]
        if any(type(value) not in (int, float) or not np.isfinite(value) or value <= 0
               for value in (width, depth)):
            return None
        source = np.float32([[p["x"], p["y"]] for p in region["readingQuad"]])
        source -= np.float32([region.get("x", 0), region.get("y", 0)])
        if source.shape != (4, 2) or not np.isfinite(source).all() or not cv2.isContourConvex(source):
            return None
        transform = cv2.getPerspectiveTransform(source, np.float32([[0, 0], [width, 0], [width, depth], [0, depth]]))
        homogeneous = np.column_stack((quad, np.ones(4))) @ transform.T
        if not np.isfinite(homogeneous).all() or np.any(np.abs(homogeneous[:, 2]) < 1e-9) or np.ptp(np.sign(homogeneous[:, 2])):
            return None
        plane = (homogeneous[:, :2] / homogeneous[:, 2:]).astype(np.float32)
        if not cv2.isContourConvex(plane) or cv2.contourArea(plane, oriented=True) <= 0:
            return None
        return plane
    except (KeyError, TypeError, ValueError, cv2.error):
        return None


def _measured_row_cells(gray, distance, quad, region=None, diagnostics=None):
    """Split supported geometry at observed seams, without a guessed count."""
    diagnostics = diagnostics if diagnostics is not None else {}
    def reject(reason):
        diagnostics["rejectedAt"] = reason
        return []
    sizes=np.linalg.norm(np.roll(quad,-1,axis=0)-quad,axis=1)
    width,height=float((sizes[0]+sizes[2])*.5),float((sizes[1]+sizes[3])*.5)
    plane = _reading_plane(quad, region)
    measured = quad if plane is None else plane
    lengths = np.linalg.norm(np.roll(measured, -1, axis=0)-measured, axis=1)
    row_aspect = float((lengths[0]+lengths[2]) / (lengths[1]+lengths[3]))
    diagnostics.update(aspectSource="image" if plane is None else "capture_plane", rowAspect=row_aspect)
    if height<MIN_FACE_WIDTH*(1.6 if plane is None else 1) or width<MIN_FACE_WIDTH*2 or row_aspect>MAX_FACES/1.6:
        return reject("row_size")
    supports=[_support(distance,quad[i],quad[(i+1)%4]) for i in range(4)]
    diagnostics["outerEdgeSupport"] = supports
    if min(supports)<.5 or np.mean(supports)<.72:
        return reject("outer_edges")
    cw=int(round(160*row_aspect))
    if cw < 16:
        return reject("row_size")
    destination=np.float32([[0,0],[cw-1,0],[cw-1,159],[0,159]])
    transform=cv2.getPerspectiveTransform(quad,destination)
    face=cv2.warpPerspective(gray,transform,(cw,160)).astype(np.float32)
    # Keep seam evidence above the printed-label quarter; printing can obscure
    # the bottom of a real seam or supply misleading short dark strokes.
    left,right,center=face[12:115,:-8],face[12:115,8:],face[12:115,4:-4]
    valley=(left+right)*.5-center
    valley_score=(valley>2).mean(axis=0)
    step=right-left
    positive=(step>8).mean(axis=0);negative=(step<-8).mean(axis=0)
    step_score=np.maximum(positive,negative)
    strong=((valley_score>=.65)&((valley>4).mean(axis=0)>=.35)) | (step_score>=.8)
    indices=np.flatnonzero(strong)+4
    groups=np.split(indices,np.flatnonzero(np.diff(indices)>2)+1) if len(indices) else []
    boundaries=[0.0];strengths=[]
    for group in groups:
        if not len(group) or group[0]<8 or group[-1]>cw-9:
            continue
        score=np.maximum(valley_score[group-4],step_score[group-4])
        strongest=group[score>=score.max()-.02]
        boundary=float(strongest.mean())
        boundaries.append(boundary);strengths.append(float(score.max()))
    boundaries.append(float(cw-1))
    count=len(boundaries)-1
    diagnostics.update(measuredFaceCount=count, seamCoverage=strengths)
    if not 2<=count<=MAX_FACES:
        return reject("seam_count")
    inverse=np.linalg.inv(transform);cells=[]
    for left,right in zip(boundaries,boundaries[1:]):
        if not 1.6<=159/(right-left)<=2.6:
            return reject("partition_aspect")
        cell=cv2.perspectiveTransform(np.float32([[[left,0],[right,0],[right,159],[left,159]]]),inverse)[0]
        lengths=np.linalg.norm(np.roll(cell,-1,axis=0)-cell,axis=1)
        cell_width=float(min(lengths[0],lengths[2]));cell_depth=float((lengths[1]+lengths[3])*.5)
        physical = _reading_plane(cell, region)
        physical_lengths = lengths if physical is None else np.linalg.norm(np.roll(physical, -1, axis=0)-physical, axis=1)
        face_aspect = float((physical_lengths[1]+physical_lengths[3])/(physical_lengths[0]+physical_lengths[2]))
        if cell_width<MIN_FACE_WIDTH or not 1.6<=face_aspect<=2.6:
            return reject("face_size_or_aspect")
        cells.append((cell,{"edgeSupport":float(np.mean(supports+strengths)),"minimumEdgeSupport":float(min(supports+strengths)),
            "estimatedStudDiameterPx":.3*cell_width,"widthPx":cell_width,"lengthPx":cell_depth,
            "geometrySource":"reading_frame_edge_seams" if plane is None else "capture_plane_edge_seams", "faceAspect":face_aspect,
            "rowFaceCount":count,"rowEnvelope":quad.tolist(),
            "minimumSeamCoverage":min(strengths)}))
    diagnostics["rejectedAt"] = None
    # Several horizontal edges may describe the same row: the top-surface
    # boundary and the lower edge of its vertical side. Prefer a consistent
    # 2x4 footprint on the capture plane, not whichever shadow has more edges.
    if plane is not None:
        shape_error = float(np.mean([abs(np.log(quality["faceAspect"] / 2.0)) for _, quality in cells]))
        diagnostics["rowGeometryError"] = shape_error
        for _, quality in cells:
            quality["rowGeometryError"] = shape_error
    return cells


def _edge_row_fallback(gray, distance, region, diagnostics=None, capture_quad=None):
    """Bounded local fragment pairing, used only after ordinary proposals fail."""
    diagnostics = diagnostics if diagnostics is not None else {}
    diagnostics.update(attempted=False, envelopeCandidates=0, acceptedFaceProposals=0)
    if not isinstance(region,dict) or region.get("readingOrientation")!="labels_toward_viewer":
        diagnostics["reason"]="missing_reading_frame"
        return []
    try:
        reading=np.float32([[p["x"],p["y"]] for p in region["readingQuad"]])
        if reading.shape!=(4,2) or not np.isfinite(reading).all() or not cv2.isContourConvex(reading):
            return []
    except (KeyError,TypeError,ValueError):
        return []
    across=(reading[1]-reading[0])+(reading[2]-reading[3]);down=(reading[3]-reading[0])+(reading[2]-reading[1])
    if min(np.linalg.norm(across),np.linalg.norm(down))<1:
        return []
    across/=np.linalg.norm(across);down/=np.linalg.norm(down)
    region_origin = np.float32([region.get("x", 0), region.get("y", 0)])
    metric_plane = _reading_plane(reading - region_origin, region) is not None
    diagnostics["attempted"]=True
    raw=cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(gray)[0]
    if raw is None:
        return []
    horizontal=[];vertical=[]
    for line in raw[:,0]:
        a,b=line[:2],line[2:];length=float(np.linalg.norm(b-a))
        if capture_quad is not None and any(cv2.pointPolygonTest(capture_quad,(float(p[0]),float(p[1])),True)<=3 for p in (a,b)):
            continue
        if length<MIN_FACE_WIDTH*.7:
            continue
        direction=(b-a)/length
        if abs(float(np.dot(direction,across)))>.96:horizontal.append((a,b,length))
        if abs(float(np.dot(direction,down)))>.96:vertical.append((a,b,length))
    horizontal=sorted(horizontal,key=lambda x:x[2],reverse=True)[:64]
    vertical=sorted(vertical,key=lambda x:x[2],reverse=True)[:32]
    groups=[]
    for a,b,_ in horizontal:
        direction=(b-a)/np.linalg.norm(b-a)
        if np.dot(direction,across)<0:direction=-direction
        normal=np.float32([-direction[1],direction[0]])
        points=[]
        for c,d,length in horizontal:
            if abs(float(np.dot((d-c)/length,direction)))>.985 and max(abs(float(np.dot(c-a,normal))),abs(float(np.dot(d-a,normal))))<3:
                points.extend((c,d))
        vx,vy,px,py=cv2.fitLine(np.float32(points),cv2.DIST_L2,0,.01,.01).reshape(-1)
        vector=np.float32([vx,vy]);origin=np.float32([px,py])
        if np.dot(vector,across)<0:vector=-vector
        projection=(np.float32(points)-origin)@vector
        start,end=origin+projection.min()*vector,origin+projection.max()*vector
        if any(abs(float(np.cross(v,start-p)))<3 and abs(float(np.dot(v,vector)))>.995 for p,v,_ in groups):
            continue
        groups.append((start,vector,float(np.linalg.norm(end-start))))
    groups=sorted(groups,key=lambda g:g[2],reverse=True)[:16]
    diagnostics.update(rowBoundaryGroups=len(groups),depthFragments=len(vertical))
    def cross(a,u,b,v):
        try:return a+np.linalg.solve(np.column_stack((u,-v)),b-a)[0]*u
        except np.linalg.LinAlgError:return None
    results=[];attempts=0
    for index,(a,u,_) in enumerate(groups):
        for b,v,_ in groups[index+1:]:
            if abs(float(np.dot(u,v)))<.985:
                continue
            top,bottom=(a,u),(b,v)
            if np.dot(a,down)>np.dot(b,down):top,bottom=bottom,top
            separation=float(np.dot(bottom[0]-top[0],down))
            if not MIN_FACE_WIDTH*1.6<=separation<gray.shape[0]*.8:
                continue
            sides=[]
            for c,d,length in vertical:
                axis=(d-c)/length
                p=cross(top[0],top[1],c,axis);q=cross(bottom[0],bottom[1],c,axis)
                if p is None or q is None:continue
                start,end=sorted((float(np.dot(p-c,axis)),float(np.dot(q-c,axis))))
                # A stud may interrupt a rear corner. Complete at most the
                # unobserved 45% of a side when capture-plane geometry exists;
                # retain measured-span and edge-support checks below.
                extension = .45 if metric_plane else .3
                if min(end,length)-max(start,0)<.55*(end-start) or start<-extension*(end-start) or end>length+extension*(end-start):continue
                if _support(distance,p,q)<.54:continue
                sides.append((p,q))
            sides.sort(key=lambda s:float(np.dot((s[0]+s[1])*.5,across)))
            for i,(tl,bl) in enumerate(sides):
                for tr,br in sides[i+1:]:
                    if np.linalg.norm(tr-tl)<MIN_FACE_WIDTH*2:continue
                    attempts+=1
                    if attempts>128:
                        diagnostics["reason"]="candidate_limit"
                        return []
                    quad=np.float32([tl,tr,br,bl])
                    if not cv2.isContourConvex(quad) or cv2.contourArea(quad,oriented=True)<=0:continue
                    if (quad[:,0]<4).any() or (quad[:,0]>gray.shape[1]-5).any() or (quad[:,1]<4).any() or (quad[:,1]>gray.shape[0]-5).any():continue
                    diagnostics["envelopeCandidates"]+=1
                    partition = {"quad": (quad + region_origin).tolist()}
                    results.extend(_measured_row_cells(gray,distance,quad,region,partition))
                    if len(diagnostics.setdefault("partitions", [])) < 16:
                        diagnostics["partitions"].append(partition)
    diagnostics["acceptedFaceProposals"]=len(results)
    diagnostics["reason"]="ok" if results else "no_supported_row_and_seams"
    return results


def locate_faces(frame_bgr, region=None):
    """Return ordered full-image quads; status=retry_needed means reject the whole word."""
    started=perf_counter()
    if not isinstance(frame_bgr,np.ndarray) or frame_bgr.dtype!=np.uint8 or frame_bgr.ndim not in (2,3):
        return _retry("invalid_image","A valid grayscale or BGR image is required.")
    h,w=frame_bgr.shape[:2]
    if min(h,w)<32 or max(h,w)>4096 or (frame_bgr.ndim==3 and frame_bgr.shape[2]!=3):
        return _retry("invalid_image","Image dimensions or channels are unsupported.")
    if region is None:x,y,rw,rh=0,0,w,h
    else:
        try:x,y,rw,rh=[region[k] for k in ("x","y","width","height")]
        except (TypeError,KeyError):return _retry("invalid_region","A valid capture region is required.")
        if any(type(v) is not int for v in (x,y,rw,rh)) or x<0 or y<0 or rw<32 or rh<32 or x+rw>w or y+rh>h:
            return _retry("invalid_region","The capture region does not match this image.")
    crop=frame_bgr[y:y+rh,x:x+rw]
    try:
        capture_quad = capture_selection_quad(region, (x, y))
    except (ValueError, TypeError, cv2.error):
        return _retry("invalid_reading_geometry", "The saved capture box is invalid. Capture again.")
    selection = {"mode": "reading_quad" if capture_quad is not None else "legacy_rectangle",
                 "inside": 0, "outside": 0, "crossing": 0, "ignoredBoundaryLines": 0,
                 "boundaryMarginPixels": 2 if capture_quad is not None else 0}
    gray=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY) if crop.ndim==3 else crop
    gray=cv2.GaussianBlur(gray,(3,3),.55)
    edges=cv2.Canny(gray,8,24)
    distance=cv2.distanceTransform(255-edges,cv2.DIST_L2,3)
    proposals=[];ignored_boundary_faces=[]
    for contour in cv2.findContours(edges,cv2.RETR_LIST,cv2.CHAIN_APPROX_SIMPLE)[0]:
        if cv2.contourArea(contour)<1000:continue
        q=cv2.approxPolyDP(contour,.025*cv2.arcLength(contour,True),True)
        if len(q)!=4 or not cv2.isContourConvex(q):continue
        q=_ordered(q)
        if _quality(q,distance):proposals.append(q)
    lines=_line_quads(gray,distance,capture_quad)
    if lines is None:return _retry("scene_too_complex","Too many edges to locate every brick reliably. Clear the word area.")
    proposals+=lines
    touching, envelopes, clipped_envelopes = _touching_row_quads(gray, distance)
    if capture_quad is not None:
        envelopes = [q for q in envelopes if capture_face_membership(q, capture_quad) != "outside"]
        clipped_envelopes = [q for q in clipped_envelopes if capture_face_membership(q, capture_quad) != "outside"]
    individual = []
    for q in proposals:
        # A broad row outline can otherwise pass as one sideways 2x4 face.
        if isinstance(region, dict) and region.get("readingOrientation") == "labels_toward_viewer" and any(cv2.intersectConvexConvex(q, envelope)[0] / max(cv2.contourArea(q), 1) > .85
               and cv2.contourArea(q) > .7 * cv2.contourArea(envelope) for envelope in envelopes):
            continue
        individual.append((q, _quality(q,distance)))
    individual = _select_capture_faces(individual, capture_quad, selection, ignored_boundary_faces)
    touching = _select_capture_faces(touching, capture_quad, selection, ignored_boundary_faces)
    edge_fallback=[];fallback_info={"attempted":False}
    if not individual and not touching:
        edge_fallback=_edge_row_fallback(gray,distance,region,fallback_info,capture_quad)
        edge_fallback = _select_capture_faces(edge_fallback, capture_quad, selection, ignored_boundary_faces)
    ranked=sorted(individual+touching+edge_fallback,key=lambda item:(item[1].get("rowFaceCount",0),
                  -item[1].get("rowGeometryError",0),item[1]["edgeSupport"]),reverse=True)
    unique=[]
    ambiguous=False
    for q,quality in ranked:
        area=float(cv2.contourArea(q)); duplicate=False
        for previous,_ in unique:
            overlap=float(cv2.intersectConvexConvex(q,previous)[0]);smaller=min(area,float(cv2.contourArea(previous)))
            if overlap/smaller>.85 and abs(area-cv2.contourArea(previous))/max(area,cv2.contourArea(previous))<.25:
                duplicate=True;break
            if overlap/smaller>.22:ambiguous=True
        if not duplicate:unique.append((q,quality))
    cells=[]
    for q,quality in sorted(unique,key=lambda item:float(item[0][:,0].mean())):
        full=q+np.float32([x,y]);cells.append({"row_id":"word","slot_index":len(cells),"quad":full.tolist(),
            "orientationCandidates":[full.tolist(),np.roll(full,-2,axis=0).tolist()],**quality})
    diagnostics={"proposalCount":len(individual)+len(touching)+len(edge_fallback),"touchingRowProposalCount":len(touching),
                 "edgeFallbackProposalCount":len(edge_fallback),"edgeFallback":fallback_info,
                 "faceCount":len(cells),"captureSelection":selection,"latencyMs":round((perf_counter()-started)*1000,3)}
    if not cells:return _retry("no_faces","No complete brick faces were located. Use distinct, well-lit, separated braille bricks.",**diagnostics)
    if clipped_envelopes and isinstance(region, dict) and region.get("readingOrientation") == "labels_toward_viewer":
        for envelope in clipped_envelopes:
            if capture_quad is not None and capture_face_membership(envelope,capture_quad)!="inside":continue
            if _is_clipped_continuation(envelope,unique,distance):
                return _retry("clipped_face","The touching row continues outside the capture boundary. Center the whole word.",cells,**diagnostics)
    if len(cells)>MAX_FACES:return _retry("too_many_faces","More than one word group may be visible.",cells,**diagnostics)
    if ambiguous:return _retry("ambiguous_faces","Overlapping face boundaries are ambiguous. Separate the bricks slightly.",cells,**diagnostics)
    for q,_ in unique:
        if (q[:,0]<4).any() or (q[:,0]>rw-5).any() or (q[:,1]<4).any() or (q[:,1]>rh-5).any():
            return _retry("clipped_face","A possible brick touches the capture boundary. Center the whole word.",cells,**diagnostics)
    centers=np.float32([np.asarray(c["quad"]).mean(axis=0) for c in cells])
    if len(cells)>1:
        if np.ptp(centers[:,0])<MIN_FACE_WIDTH*.5:
            return _retry("multiple_rows","Use one row read from image left to right.",cells,**diagnostics)
        slope,intercept=np.polyfit(centers[:,0],centers[:,1],1)
        error=np.abs(centers[:,1]-(slope*centers[:,0]+intercept))
        lengths=np.array([c["lengthPx"] for c in cells]);widths=np.array([c["widthPx"] for c in cells])
        if abs(slope)>.7 or error.max()>.24*np.median(lengths):
            return _retry("multiple_rows","Multiple rows or uncertain word order. Build one clear row.",cells,**diagnostics)
        coherent_touching = all(c.get("rowFaceCount") == len(cells) and c.get("rowEnvelope") == cells[0].get("rowEnvelope") for c in cells)
        if not coherent_touching and (lengths.max()/lengths.min()>1.4 or widths.max()/widths.min()>1.4):
            return _retry("inconsistent_faces","Face scale is inconsistent; a face may be missing or misidentified.",cells,**diagnostics)
        spacings=np.linalg.norm(np.diff(centers,axis=0),axis=1)
        if spacings.max()>2.2*np.median(widths):
            return _retry("word_gap","A large gap may contain a missed brick or another word.",cells,**diagnostics)
    # A strong unassigned long boundary beside the proposed row may be an
    # omitted end/middle brick. Never silently return just the easy subset.
    raw_lines=cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(gray)[0]
    if raw_lines is not None:
        mask=np.zeros(gray.shape,np.uint8)
        for q,_ in unique:cv2.fillConvexPoly(mask,np.rint(q).astype(np.int32),255)
        margin=max(4,int(np.median([c["widthPx"] for c in cells])*.12))
        mask=cv2.dilate(mask,np.ones((2*margin+1,2*margin+1),np.uint8))
        axes=[]
        for q,_ in unique:
            axis=q[3]-q[0];axes.append(axis/np.linalg.norm(axis))
        typical=float(np.median([c["lengthPx"] for c in cells]))
        middle=float(np.median(centers[:,1]))-y
        unexplained=[]
        for line in raw_lines[:,0]:
            a,b=line[:2],line[2:];v=b-a;length=float(np.linalg.norm(v))
            if not _segment_intersects_capture(a, b, capture_quad):continue
            if capture_quad is not None:
                boundary = min(cv2.pointPolygonTest(capture_quad,(float(p[0]),float(p[1])),True) for p in (a,b))
                inside_ignored = any(all(cv2.pointPolygonTest(q,(float(p[0]),float(p[1])),True)>=-2 for p in (a,b))
                                     for q in ignored_boundary_faces)
                if boundary<=3 or inside_ignored or _boundary_connected_fragment(a,b,capture_quad,distance,
                        float(np.median([c["widthPx"] for c in cells]))*1.25):
                    selection["ignoredBoundaryLines"] += 1
                    continue
            if length<MIN_FACE_WIDTH or length>typical*2.5:continue
            d=v/length
            if max(abs(float(np.dot(d,axis))) for axis in axes)<.96:continue
            if abs(float((a[1]+b[1])*.5)-middle)>typical*.65:continue
            samples=np.rint(np.linspace(a,b,24)).astype(int)
            samples[:,0]=np.clip(samples[:,0],0,rw-1);samples[:,1]=np.clip(samples[:,1],0,rh-1)
            outside=float((mask[samples[:,1],samples[:,0]]==0).mean())
            if outside>.7:unexplained.append((line+np.float32([x,y,x,y])).tolist())
        if unexplained:
            diagnostics["unassignedBoundaryCount"]=len(unexplained)
            diagnostics["unassignedBoundaries"]=unexplained[:32]
            return _retry("unresolved_face_boundaries","Other possible brick boundaries were not resolved. No partial word will be accepted.",cells,**diagnostics)
    return {"status":"localized","reason":"ok","message":"Grayscale face proposals; dot/orientation validation is still required.",
            "cells":cells,"method":"grayscale_edge_face_geometry","completenessVerified":False,"diagnostics":diagnostics}
