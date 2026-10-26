import { ArcRotateCamera, Engine, HemisphericLight, MeshBuilder, Scene, Vector3 } from "@babylonjs/core";
export { puzzleSteps } from "./metrics";
const canvas = document.querySelector<HTMLCanvasElement>("#game")!;
const engine = new Engine(canvas, true); const scene = new Scene(engine);
new ArcRotateCamera("camera", Math.PI / 2, Math.PI / 3, 5, Vector3.Zero(), scene).attachControl(canvas, true);
new HemisphericLight("light", new Vector3(0, 1, 0), scene); MeshBuilder.CreateBox("fixture", {}, scene);
engine.runRenderLoop(() => scene.render());

scene.executeWhenReady(() => { scene.render(); document.body.dataset.fixtureReady = "true"; });
