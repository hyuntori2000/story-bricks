"""Offline card queue/provider contract checks. No credentials or external requests."""
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
import zlib

from comfy_word_images import (ComfyCardProvider, ImageError, WordImageJobs, validate_png,
                               MAX_IMAGE_BYTES, _public_https)
from workspace_storage import read_json, write_json


def png(width=1, height=1):
    def chunk(kind, body):
        return struct.pack('>I',len(body))+kind+body+struct.pack('>I',zlib.crc32(kind+body)&0xffffffff)
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(b'\0\xff\x11\x33\xff'))+chunk(b'IEND',b'')


def request(word='MOON.', request_id='r1', group='g1', workspace='judy'):
    return dict(workspaceId=workspace,experienceSessionId='a'*32,sessionId='session-1',requestId=request_id,groupId=group,word=word)


def query(value):
    return {k:[value[k]] for k in ('workspaceId','experienceSessionId','sessionId','requestId')}


class FakeProvider:
    configured=True
    def __init__(self, block=False, error=False):
        self.submitted=[];self.finished=[];self.release=threading.Event();self.error=error
        if not block:self.release.set()
    def submit(self, word):
        self.submitted.append(word)
        return 'prompt-'+str(len(self.submitted))
    def finish(self, prompt_id):
        self.finished.append(prompt_id)
        self.release.wait(3)
        if self.error:raise RuntimeError('secret-token-provider-body')
        return png()


class ManagerTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.managers=[];self.providers=[]
    def tearDown(self):
        for p in self.providers:p.release.set()
        for m in self.managers:
            m.queue.join();m.close()
        self.temp.cleanup()
    def manager(self, provider=None, **options):
        provider=provider or FakeProvider();self.providers.append(provider)
        m=WordImageJobs(provider,Path(self.temp.name),**options);self.managers.append(m)
        return m,provider
    def ready(self,m,value):
        for _ in range(150):
            result=m.status(query(value))
            if result['status'] in ('ready','failed'):return result
            time.sleep(.01)
        self.fail('Job did not finish')
    def test_async_single_submit_and_owned_reply(self):
        m,p=self.manager(FakeProvider(block=True));value=request()
        start=time.monotonic();first=m.submit(value)
        self.assertLess(time.monotonic()-start,.25)
        self.assertEqual(first['status'],'queued')
        m.submit(value);m.submit(request(request_id='r2'))
        p.release.set();result=self.ready(m,value)
        self.assertEqual(result['status'],'ready');self.assertEqual(p.submitted,['MOON.'])
        self.assertEqual(m.read_image(result['imagePath']),png())
        for field in value:self.assertEqual(result[field],value[field])
        self.assertEqual(m.status(query(request(request_id='r2')))['requestId'],'r2')
    def test_request_and_group_conflicts(self):
        m,p=self.manager();m.submit(request())
        for changed in (request(word='FOX'),request(word='FOX',request_id='r2')):
            with self.assertRaises(ImageError) as caught:m.submit(changed)
            self.assertEqual(caught.exception.status,409)
    def test_owner_isolation(self):
        m,p=self.manager();m.submit(request());m.submit(request(workspace='demo'))
        self.ready(m,request());self.ready(m,request(workspace='demo'))
        self.assertEqual(len(p.submitted),2)
        self.assertNotEqual(m.status(query(request()))['imagePath'],m.status(query(request(workspace='demo')))['imagePath'])
    def test_missing_owner_and_duplicate_query_rejected(self):
        m,p=self.manager();v=request();v.pop('workspaceId')
        with self.assertRaises(ImageError):m.submit(v)
        q=query(request());q['requestId']=['r1','r2']
        with self.assertRaises(ImageError):m.status(q)
        self.assertEqual(p.submitted,[])
    def test_finish_ignored_abstract_word_attempted(self):
        m,p=self.manager();dot=request(word='.',group='dot')
        self.assertEqual(m.submit(dot)['code'],'image_finish_ignored')
        wind=request(word='WIND',group='wind',request_id='wind')
        m.submit(wind);self.assertEqual(self.ready(m,wind)['status'],'ready')
        self.assertEqual(p.submitted,['WIND'])
    def test_queue_bounded_and_duplicates_still_readable(self):
        m,p=self.manager(FakeProvider(block=True),workers=1,max_pending=1)
        m.submit(request());self.assertIn(m.submit(request())['status'],('queued','running'))
        with self.assertRaises(ImageError) as caught:m.submit(request(group='g2',request_id='r2'))
        self.assertEqual(caught.exception.code,'image_queue_full')
    def test_exception_body_not_persisted(self):
        m,p=self.manager(FakeProvider(error=True));m.submit(request())
        result=self.ready(m,request());self.assertEqual(result['status'],'failed')
        for path in Path(self.temp.name).rglob('*.json'):
            self.assertNotIn('secret-token',path.read_text())
    def test_restart_ready_and_failed_no_resubmit(self):
        m,p=self.manager();m.submit(request());self.ready(m,request());m.close()
        restarted,p2=self.manager();result=restarted.submit(request())
        self.assertEqual(result['status'],'ready');self.assertEqual(p2.submitted,[])
        self.assertEqual(restarted.read_image(result['imagePath']),png())
    def test_restart_resumes_known_prompt_only(self):
        m,p=self.manager();m.submit(request());self.ready(m,request());m.close()
        record=next(Path(self.temp.name).glob('judy/sessions/*/images/*.json'))
        job=read_json(record);job.update(status='running',imagePath='',promptId='existing-prompt');write_json(record,job)
        resumed,p2=self.manager();self.assertEqual(self.ready(resumed,request())['status'],'ready')
        self.assertEqual(p2.submitted,[]);self.assertEqual(p2.finished,['existing-prompt'])
    def test_restart_missing_prompt_never_resubmits(self):
        m,p=self.manager();m.submit(request());self.ready(m,request());m.close()
        record=next(Path(self.temp.name).glob('judy/sessions/*/images/*.json'))
        job=read_json(record);job.update(status='running',imagePath='',promptId='');write_json(record,job)
        resumed,p2=self.manager();result=resumed.submit(request())
        self.assertEqual(result['code'],'image_interrupted');self.assertEqual(p2.submitted,[])
    def test_download_path_traversal_rejected(self):
        m,p=self.manager()
        with self.assertRaises(ImageError):m.read_image('/word-images/../../secret.png')


class ProviderTests(unittest.TestCase):
    def test_workflow_preserves_model_nodes_and_style(self):
        p=ComfyCardProvider('never-real');calls=[]
        p._json=lambda path,body=None:(calls.append((path,body)) or {'prompt_id':'p1'})
        self.assertEqual(p.submit('WIND.'),'p1')
        workflow=calls[0][1]['prompt']
        self.assertEqual(workflow['104']['inputs']['model'],'gemini-3-pro-image-preview')
        self.assertTrue(workflow['104']['inputs']['prompt'].endswith('one single wind'))
        self.assertEqual(workflow['106']['inputs']['images'],['128:16',0])
        self.assertEqual(workflow['128:14']['inputs']['bg_removal_name'],'birefnet.safetensors')
        self.assertNotIn('never-real',repr(p))
    def test_only_final_node_106_image_selected(self):
        history={'outputs':{'104':{'images':[{'filename':'preview.png'}]},'106':{'images':[{'filename':'final.png','type':'output'}]}}}
        self.assertEqual(ComfyCardProvider._output(history,'p')['filename'],'final.png')
        history['outputs'].pop('106')
        self.assertIsNone(ComfyCardProvider._output(history,'p'))
    def test_api_key_never_forwarded_to_signed_url(self):
        p=ComfyCardProvider('never-real');calls=[]
        def js(path,body=None):
            if '/status' in path:return {'status':'success'}
            return {'outputs':{'106':{'images':[{'filename':'final.png','type':'output'}]}}}
        p._json=js
        def fetch(url,**kwargs):
            calls.append((url,kwargs['authenticated']))
            return (None,'https://cdn.example/card.png') if len(calls)==1 else (png(),None)
        p._request=fetch
        with patch('comfy_word_images._public_https',lambda u:u):self.assertEqual(p.finish('p1'),png())
        self.assertEqual([entry[1] for entry in calls],[True,False])
    def test_unapproved_origin_and_private_download_rejected(self):
        with self.assertRaises(ImageError):ComfyCardProvider('never-real','https://example.com')
        for url in ('http://example.com/x','https://user:pass@example.com/x','https://localhost:123/x'):
            with self.assertRaises(ImageError):_public_https(url)
        with patch('socket.getaddrinfo',return_value=[(2,1,6,'',('127.0.0.1',443))]):
            with self.assertRaises(ImageError):_public_https('https://cdn.example/x')
    def test_png_limits_and_integrity(self):
        self.assertEqual(validate_png(png()),(1,1))
        for data in (b'not png',png()[:-1],png(width=4097),png()+b'extra',b'x'*(MAX_IMAGE_BYTES+1)):
            with self.assertRaises(ImageError):validate_png(data)
        broken=bytearray(png());broken[45]^=1
        with self.assertRaises(ImageError):validate_png(bytes(broken))


if __name__=='__main__':unittest.main()
