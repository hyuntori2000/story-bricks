import json
from pathlib import Path
import unittest
import cv2
import numpy as np
from braille_face_locator import locate_faces

PROJECT=Path(__file__).resolve().parents[1]
SOURCE=PROJECT.parent/'LEGO_OCR'


def row_image(xs=(40,120,200),shape=(240,360),ys=None):
    image=np.full((*shape,3),70,np.uint8)
    for i,x in enumerate(xs):
        y=40 if ys is None else ys[i]
        cv2.rectangle(image,(x,y),(x+55,y+120),(210,210,210),-1)
        cv2.circle(image,(x+14,y+16),8,(100,100,100),2)
    return image


def photographed_reference_fixture():
    # Derived fixture only. Supplied corners construct the test image, never
    # enter locate_faces. It is not an independent Quest recognition evaluation.
    image=cv2.imread(str(SOURCE/'samples/lego.png'))
    layout=json.loads((SOURCE/'layouts/lego_braille.json').read_text())
    canvas=np.full((220,340,3),70,np.uint8)
    for x,index in zip((30,130,230),(5,14,23)):
        q=np.float32(layout['cells'][index]['quad'])
        destination=np.float32([[0,0],[69,0],[69,139],[0,139]])
        face=cv2.warpPerspective(image,cv2.getPerspectiveTransform(q,destination),(70,140))
        canvas[40:180,x:x+70]=face
    return canvas


def touching_row_image(count, dark=False, seams=True, missing_seam=None, transform=None):
    background, surfaces, seam = (220,(80,95,110),35) if dark else (55,(180,200,220),100)
    width, height = count*60+100, 260
    image=np.full((height,width,3),background,np.uint8)
    for index in range(count):
        x=50+index*60
        cv2.rectangle(image,(x,65),(x+60,185),(surfaces[index%3],)*3,-1)
        cv2.circle(image,(x+16,87),9,(surfaces[index%3]-25,)*3,2)
    if seams:
        for index in range(1,count):
            if index != missing_seam:
                cv2.line(image,(50+60*index,65),(50+60*index,185),(seam,)*3,2)
    corners=np.float32([[[50+index*60,65],[110+index*60,65],[110+index*60,185],[50+index*60,185]] for index in range(count)])
    if transform=='rotation':
        matrix=cv2.getRotationMatrix2D((width*.5,height*.5),8,1)
        image=cv2.warpAffine(image,matrix,(width,height),borderValue=(background,)*3)
        corners=cv2.transform(corners,matrix)
    elif transform=='perspective':
        source=np.float32([[50,65],[width-50,65],[width-50,185],[50,185]])
        target=np.float32([[55,70],[width-65,55],[width-40,190],[40,180]])
        matrix=cv2.getPerspectiveTransform(source,target)
        image=cv2.warpPerspective(image,matrix,(width,height),borderValue=(background,)*3)
        corners=cv2.perspectiveTransform(corners,matrix)
    region={'x':0,'y':0,'width':width,'height':height,'readingOrientation':'labels_toward_viewer'}
    return image,region,corners.mean(axis=1)


class BrailleFaceLocatorTests(unittest.TestCase):
    def _archived_frame(self, stem):
        archive=PROJECT.parents[3]/'2026-11-14/story-bricks-dot-recognition/outputs/capturedimage2026'
        sidecar=archive/(stem+'.json')
        if not sidecar.exists():
            self.skipTest('Optional local Quest archive is unavailable')
        saved=json.loads(sidecar.read_text())
        return cv2.imread(str(archive/saved['imageFile'])),saved['metadata']

    def test_table_background_cannot_veto_a_complete_archived_row(self):
        image,metadata=self._archived_frame('20261114T2342230121450Z_b7fe2194b482a03ef8a6c6f7551ab6e8b1b155f40fb238198a3be28403ad0e2c')
        result=locate_faces(image,metadata['recognitionRegion'])
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),3)

    def test_mixed_gray_archived_row_uses_supported_edges_and_four_measured_faces(self):
        image,metadata=self._archived_frame('20261114T2342489982620Z_e0b1922286aa71197a8dea891aefeb936a254b9ba8d884fe29908f3d9f495578')
        result=locate_faces(image,metadata['recognitionRegion'])
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),4)
        self.assertTrue(all(c.get('geometrySource')=='reading_frame_edge_seams' for c in result['cells']))
        self.assertTrue(result['diagnostics']['edgeFallback']['attempted'])

    def test_edge_fallback_does_not_turn_label_strokes_into_face_seams(self):
        from braille_face_locator import _measured_row_cells
        image=np.full((220,300),60,np.uint8)
        cv2.rectangle(image,(30,30),(270,190),180,-1)
        for x in (110,190):
            cv2.line(image,(x,150),(x,190),40,3)
        gray=cv2.GaussianBlur(image,(3,3),.55)
        distance=cv2.distanceTransform(255-cv2.Canny(gray,8,24),cv2.DIST_L2,3)
        result=_measured_row_cells(gray,distance,np.float32([[30,30],[270,30],[270,190],[30,190]]))
        self.assertEqual(result,[])

    def test_touching_rows_two_through_eight_use_visible_seams_in_both_polarities(self):
        for count in range(2,9):
            for dark in (False,True):
                for transform in (None,'rotation','perspective'):
                    with self.subTest(count=count,dark=dark,transform=transform):
                        image,region,centers=touching_row_image(count,dark=dark,transform=transform)
                        result=locate_faces(image,region)
                        self.assertEqual(result['status'],'localized',result)
                        self.assertEqual(len(result['cells']),count,result)
                        actual=np.float32([np.mean(c['quad'],axis=0) for c in result['cells']])
                        self.assertLess(float(np.max(np.linalg.norm(actual-centers,axis=1))),5)

    def test_upright_unseamed_slab_does_not_become_a_sideways_face(self):
        for count in range(2,9):
            image,region,_=touching_row_image(count,seams=False)
            # Remove brightness transitions as well: no measured internal seams.
            cv2.rectangle(image,(50,65),(50+count*60,185),(200,200,200),-1)
            result=locate_faces(image,region)
            self.assertEqual(result['status'],'retry_needed',(count,result))

    def test_missing_seam_rejects_a_partial_touching_row(self):
        image,region,_=touching_row_image(5,missing_seam=2)
        # A shared flat surface replaces the missing physical boundary.
        cv2.rectangle(image,(111,65),(229,185),(200,200,200),-1)
        result=locate_faces(image,region)
        self.assertEqual(result['status'],'retry_needed',result)

    def test_touching_row_clipped_at_region_boundary_is_rejected(self):
        image,region,_=touching_row_image(4)
        region.update(x=70,width=image.shape[1]-70)
        self.assertEqual(locate_faces(image,region)['status'],'retry_needed')

    def test_saved_touching_quest_row_localizes_without_saved_face_corners(self):
        archive=PROJECT.parents[3]/'2026-11-14/story-bricks-dot-recognition/outputs/capturedimage2026'
        sidecar=archive/'20261114T2326014408980Z_b749b218f523b4b771794245a03bcf1125528c04953a618e9426f5cb6597dc5e.json'
        if not sidecar.exists():
            self.skipTest('Optional local Quest archive is unavailable')
        saved=json.loads(sidecar.read_text())
        image=cv2.imread(str(archive/saved['imageFile']))
        result=locate_faces(image,saved['metadata']['recognitionRegion'])
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),3)
        self.assertTrue(all(c.get('geometrySource')=='touching_row_visible_seams' for c in result['cells']))

    def test_three_separated_faces_are_ordered_and_complete_in_fixture(self):
        result=locate_faces(row_image())
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),3)
        centers=[np.mean(c['quad'],axis=0) for c in result['cells']]
        self.assertTrue(np.all(np.diff(np.array(centers)[:,0])>0))
        self.assertFalse(result['completenessVerified'])

    def test_orientation_candidates_preserve_two_ends_without_mirroring(self):
        result=locate_faces(row_image(xs=(80,)))
        self.assertEqual(result['status'],'localized')
        c=result['cells'][0];q=np.float32(c['quad'])
        self.assertEqual(len(c['orientationCandidates']),2)
        self.assertTrue(np.allclose(np.roll(q,-2,axis=0),c['orientationCandidates'][1]))
        for p in c['orientationCandidates']:
            self.assertGreater(cv2.contourArea(np.float32(p),oriented=True),0)

    def test_gray_and_bgr_inputs_have_identical_geometry(self):
        image=row_image()
        a=locate_faces(image);b=locate_faces(cv2.cvtColor(image,cv2.COLOR_BGR2GRAY))
        self.assertEqual(a['status'],b['status'])
        self.assertEqual([c['quad'] for c in a['cells']],[c['quad'] for c in b['cells']])

    def test_region_coordinates_remain_in_full_image(self):
        small=row_image();large=np.full((350,500,3),70,np.uint8);large[60:300,90:450]=small
        a=locate_faces(small);b=locate_faces(large,{'x':90,'y':60,'width':360,'height':240})
        self.assertEqual(b['status'],'localized')
        for ca,cb in zip(a['cells'],b['cells']):
            self.assertTrue(np.allclose(np.float32(ca['quad'])+[90,60],cb['quad'],atol=.001))

    def test_small_rotated_row_is_not_reordered(self):
        image=row_image();matrix=cv2.getRotationMatrix2D((180,120),8,1)
        rotated=cv2.warpAffine(image,matrix,(360,240),borderValue=(70,70,70))
        result=locate_faces(rotated)
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),3)

    def test_blank_and_low_resolution_have_no_accepted_faces(self):
        self.assertEqual(locate_faces(np.full((200,300,3),70,np.uint8))['status'],'retry_needed')
        image=row_image();image=cv2.resize(image,(90,60))
        self.assertEqual(locate_faces(image)['status'],'retry_needed')

    def test_invalid_input_fails_closed(self):
        for image in (None,np.zeros((200,300,4),np.uint8),np.zeros((200,300),np.float32)):
            self.assertEqual(locate_faces(image)['status'],'retry_needed')
        self.assertEqual(locate_faces(row_image(),{'x':-1,'y':0,'width':200,'height':200})['status'],'retry_needed')

    def test_multiple_rows_are_rejected(self):
        image=row_image(xs=(40,120,200,40,120,200),shape=(400,360),ys=(30,30,30,220,220,220))
        result=locate_faces(image)
        self.assertEqual(result['status'],'retry_needed',result)

    def test_large_word_gap_is_rejected(self):
        result=locate_faces(row_image(xs=(30,250),shape=(240,400)))
        self.assertEqual(result['status'],'retry_needed',result)
        self.assertEqual(result['reason'],'word_gap')

    def test_edge_clipped_brick_is_not_dropped_silently(self):
        image=row_image(xs=(0,80,160))
        result=locate_faces(image)
        self.assertEqual(result['status'],'retry_needed',result)

    def test_unresolved_neighbor_boundary_rejects_easy_subset(self):
        image=row_image(xs=(80,160));cv2.line(image,(30,40),(30,160),(210,210,210),2)
        result=locate_faces(image)
        self.assertEqual(result['status'],'retry_needed',result)
        self.assertEqual(result['reason'],'unresolved_face_boundaries')

    def test_photographed_derived_reference_faces_localize_without_runtime_corners(self):
        result=locate_faces(photographed_reference_fixture())
        self.assertEqual(result['status'],'localized',result)
        self.assertEqual(len(result['cells']),3)

    def test_touching_real_board_row_never_accepts_just_middle_letters(self):
        image=cv2.imread(str(SOURCE/'samples/lego.png'))
        result=locate_faces(image,{'x':290,'y':200,'width':450,'height':160})
        self.assertEqual(result['status'],'retry_needed',result)
        self.assertEqual(result['reason'],'unresolved_face_boundaries')


if __name__=='__main__':unittest.main()
