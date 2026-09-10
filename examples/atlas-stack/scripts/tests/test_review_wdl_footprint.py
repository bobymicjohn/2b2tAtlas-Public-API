import importlib.util
from pathlib import Path
import unittest
spec=importlib.util.spec_from_file_location('review',Path(__file__).parents[1]/'review-wdl-footprint.py')
review=importlib.util.module_from_spec(spec);spec.loader.exec_module(review)

class FootprintProposalTests(unittest.TestCase):
    def test_road_does_not_join_other_exhibit(self):
        rows=[]
        for z in range(-20,31):
            for x in range(-20,141):
                built=(0<=x<=10 and 0<=z<=10) or (100<=x<=120 and 0<=z<=20) or (10<x<100 and z in [4,5])
                rows.append((x,z,1,100 if built else 0,0,0))
        result,_=review.propose(rows,80,80)
        self.assertLess(result['bounds'][2],100*16)
        self.assertEqual(result['unsavedInsideCandidate'],0)
        self.assertGreater(result['buildChunksOutsideCandidate'],400)
        self.assertTrue(result['approvalRequiredBeforePublication'])
    def test_detached_nearby_buildings_are_flagged(self):
        rows=[(x,z,1,100 if (0<=x<=10 and 0<=z<=10) or (25<=x<=35 and 0<=z<=10) else 0,0,0)
              for x in range(-20,60) for z in range(-20,31)]
        result,_=review.propose(rows,80,80)
        self.assertGreater(result['nearbyBuildChunksOutsideCandidate'],16)
        self.assertTrue(any('detached buildings' in w for w in result['warnings']))

    def test_thin_bridge_is_not_silently_declared_complete(self):
        rows=[(x,z,1,100 if z==0 else 0,0,0) for x in range(-40,100) for z in range(-30,31)]
        result,_=review.propose(rows,0,0)
        self.assertTrue(result['warnings'])
        self.assertEqual(result['status'],'needs-footprint-review')
    def test_wrong_merged_location_center_rejected(self):
        with self.assertRaisesRegex(ValueError,'landing'):
            review.propose([(0,0,1,100,0,0)],-7715,6556)
    def test_negative_coordinates_and_unsaved_holes(self):
        rows=[(x,z,1,100,0,0) for x in range(-20,0) for z in range(-20,0) if (x,z)!=(-15,-15)]
        result,_=review.propose(rows,-160,-160)
        self.assertEqual(result['bounds'],[-320,-320,0,0])
        self.assertEqual(result['unsavedInsideCandidate'],1)
        self.assertTrue(result['warnings'])

if __name__=='__main__': unittest.main()
