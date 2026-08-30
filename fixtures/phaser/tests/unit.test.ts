import { expect, test } from "vitest";
import { scoreForTargets } from "../src/metrics";
test("fixture scoring is deterministic", () => expect(scoreForTargets(3)).toBe(300));
