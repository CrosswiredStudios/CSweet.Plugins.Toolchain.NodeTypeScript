import Phaser from "phaser";
export { scoreForTargets } from "./metrics";
class FixtureScene extends Phaser.Scene {
  create() { this.add.text(24, 24, "C-Sweet Phaser 4.1 certification", { color: "#ffffff" }); }
}
new Phaser.Game({ type: Phaser.AUTO, width: 640, height: 360, parent: "game", backgroundColor: "#1b2340", scene: FixtureScene });
