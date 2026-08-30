import { expect, test } from "vitest";
import { puzzleSteps } from "../src/metrics";
test("fixture puzzle metric is deterministic", () => expect(puzzleSteps(4)).toBe(9));
