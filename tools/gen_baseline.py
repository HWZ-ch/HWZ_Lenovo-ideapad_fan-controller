#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import json, os
p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ec_baseline.json")
json.dump({"base": [0] * 256}, open(p, "w"))
print("baseline ok ->", p, " (0xFE 正常态 = 0x00)")
