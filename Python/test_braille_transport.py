"""Frame-bound dot recognition transport; no headset, microphone or external API."""
import copy
import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from camera_frame_server import FrameStore, FrameHTTPServer, RequestError
from test_camera_frame_server import sample_metadata, sample_image


def dot_result():
    return {'status':'recognized','automaticBlockDetection':True,'recognitionPerformed':True,
            'word':'ZZ','letters':['Z','Z'],'method':'local_braille_dots',
            'algorithm_id':'six_dot_radial_v1','decoder_id':'lego_english_a16_punctuation_v2',
            'coordinateSystem':'image_pixels_top_left','groupCenterPixel':{'x':8,'y':6},
            'result':{'method':'braille_dots','cells':[
                {'kind':'letter','letter_candidate':'Z','accepted':True,'face_polygon':[[1,1],[6,1],[6,10],[1,10]]},
                {'kind':'letter','letter_candidate':'Z','accepted':True,'face_polygon':[[8,1],[14,1],[14,10],[8,10]]}]}}

class BrailleTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.result=dot_result()
        self.store=FrameStore(Path(self.temp.name),lambda frame,metadata:copy.deepcopy(self.result))
    def tearDown(self): self.temp.cleanup()
    def upload(self,identifier='dots'):
        return self.store.accept(sample_metadata(identifier),sample_image())
    def test_dot_method_emits_letters_and_frozen_identity_without_color_session(self):
        ack=self.upload();self.assertTrue(ack['accepted']);self.assertIsNone(self.store.color_session)
        r=self.store.color_result('test-session','dots')
        self.assertTrue(r['ready']);self.assertEqual(r['recognitionMethod'],'braille_dots')
        self.assertEqual([d['letter'] for d in r['detections']],['Z','Z'])
        self.assertEqual(r['groupCenterPixel'],{'x':8,'y':6})
        self.assertEqual(r['captureTimestampUtc'],sample_metadata()['captureTimestampUtc'])
        self.assertEqual(r['algorithm_id'],'six_dot_radial_v1')
        self.assertEqual(r['decoder_id'],'lego_english_a16_punctuation_v2')
    def test_letters_and_period_keep_geometry_and_kind(self):
        cells=[]
        for symbol in "TREE.":
            cell=copy.deepcopy(self.result['result']['cells'][0])
            cell.update(kind='letter' if symbol.isalpha() else 'punctuation',
                        letter_candidate=symbol if symbol.isalpha() else None,
                        punctuation_candidate=symbol if not symbol.isalpha() else None)
            cells.append(cell)
        self.result['result']['cells']=cells
        self.upload()
        detections=self.store.color_result('test-session','dots')['detections']
        self.assertEqual(''.join(d['letter'] for d in detections),"TREE.")
        self.assertTrue(all(d['accepted'] for d in detections))
        self.assertEqual(detections[4]['kind'],'punctuation')
        self.assertEqual(len(detections),len(cells))
        self.assertEqual(detections[-1]['quad'][0],{'x':1.0,'y':1.0})
    def test_unknown_question_mark_indicators_and_numbers_are_rejected(self):
        cases=[{'kind':'punctuation','punctuation_candidate':symbol} for symbol in "?!',-"] + [
               {'kind':'unknown','value':'?','letter_candidate':'?'},
               {'kind':'punctuation','punctuation_candidate':'?','accepted':False},
               {'kind':'letter','letter_candidate':'?'},
               {'kind':'indicator','value':'#','letter_candidate':'A'},
               {'kind':'indicator','value':'<CAP>'},
               {'kind':'indicator','value':'<GRADE1>'},
               {'kind':'number','letter_candidate':'1'},
               {'kind':'punctuation','punctuation_candidate':'#'},
               {'kind':'punctuation','punctuation_candidate':'..'}]
        for index,fields in enumerate(cases):
            with self.subTest(fields=fields):
                cell=copy.deepcopy(dot_result()['result']['cells'][0]);cell.update(fields)
                self.result['result']['cells']=[cell]
                self.upload(str(index))
                detection=self.store.color_result('test-session',str(index))['detections'][0]
                self.assertFalse(detection['accepted'])
                self.assertEqual(detection['letter'],'?')
    def test_uncertain_dots_are_saved_as_retry_without_fabricated_word(self):
        self.result={'status':'retry_needed','automaticBlockDetection':True,'recognitionPerformed':False,
                     'word':None,'method':'local_braille_dots','message':'Ambiguous dots'}
        self.upload();r=self.store.color_result('test-session','dots')
        self.assertTrue(r['ready']);self.assertEqual(r['status'],'retry_needed');self.assertEqual(r['detections'],[])
        self.assertTrue((Path(self.temp.name)/'latest.png').exists())
    def test_new_frame_rejects_old_dot_response(self):
        self.upload('old');self.upload('new')
        with self.assertRaises(RequestError):self.store.color_result('test-session','old')
    def test_unknown_method_cannot_be_presented_as_dot_letters(self):
        self.result['result']['method']='unsupported_classifier'
        self.upload();self.assertFalse(self.store.color_result('test-session','dots')['ready'])
    def test_recognition_route_and_legacy_alias_match(self):
        self.upload();server=FrameHTTPServer(('127.0.0.1',0),self.store)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            responses=[]
            for endpoint in ['/recognition/result','/color/result']:
                connection=http.client.HTTPConnection('127.0.0.1',server.server_port,timeout=3)
                connection.request('GET',endpoint+'?sessionId=test-session&frameId=dots')
                response=connection.getresponse();self.assertEqual(response.status,200)
                responses.append(json.loads(response.read()));connection.close()
            self.assertEqual(*responses)
        finally:server.shutdown();server.server_close();thread.join()
    def test_braille_store_never_loads_color_calibration_or_falls_back(self):
        from physical_story_receiver import build_store, automatic_dashboard
        with patch('physical_story_receiver.ColorSession',side_effect=AssertionError('Color invoked')):
            store=build_store(Path(self.temp.name)/'braille',Path('/missing-color.json'),automatic=True,recognition_mode='braille')
        self.assertIsNone(store.color_session)
        self.assertEqual(store.recognizer.recognition_method,'braille_dots')
        self.assertEqual(store.recognizer.recognition_provenance['algorithm_id'],'six_dot_radial_v1')
        self.assertEqual(store.recognizer.recognition_provenance['decoder_id'],'lego_english_a16_punctuation_v2')
        # Tiny no-region input must remain a retry, without running color recognition.
        ack=store.accept(sample_metadata(),sample_image())
        self.assertTrue(ack['accepted']);self.assertEqual(ack['status'],'retry_needed')
        page=automatic_dashboard('braille').decode()
        self.assertIn('braille-dot recognition',page)
        self.assertNotIn('This color-code test',page)

if __name__=='__main__':unittest.main()
