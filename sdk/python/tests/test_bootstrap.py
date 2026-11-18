import unittest
from quantdesk_sdk import __version__

class BootstrapTests(unittest.TestCase):
    def test_version(self):
        self.assertEqual(__version__, "0.1.0")

if __name__ == "__main__":
    unittest.main()