import { describe, expect, test } from "bun:test";
import { canStart, capacityFloor, isFull, isOwner, isParticipant, openSeats, rosterColumns, seatState, type RoomView } from "./lobby";

const seat = (id: string) => ({ id, displayName: id, avatarSeed: "avatar-0", guest: false });
const room = (ids: string[], capacity = 4): RoomView => ({
  id: "r1", format: "poll", code: "ABC234", owner: ids[0] ?? "ann", capacity, settings: {}, phase: "lobby", endedReason: null,
  lobbyEndsAt: 0, participants: ids.map(seat),
});

describe("the lobby", () => {
  test("the owner is the room's owner", () => {
    expect(isOwner(room(["ann", "bob"]), "ann")).toBe(true);
    expect(isOwner(room(["ann", "bob"]), "bob")).toBe(false);
  });

  test("a non-owner is never told they can start, even with enough players", () => {
    expect(canStart(room(["ann", "bob", "cy"]), "bob")).toBe(false);
  });

  test("the owner alone cannot start", () => {
    expect(canStart(room(["ann"]), "ann")).toBe(false);
  });

  test("the owner with two or more seated can start, in a lobby of any size", () => {
    expect(canStart(room(["ann", "bob"], 2), "ann")).toBe(true);
    expect(canStart(room(["ann", "bob"], 8), "ann")).toBe(true);
  });

  test("a seated player is a participant and a visitor is not", () => {
    expect(isParticipant(room(["ann", "bob"]), "bob")).toBe(true);
    expect(isParticipant(room(["ann", "bob"]), "cy")).toBe(false);
  });

  test("a lobby is full once seated to capacity", () => {
    expect(isFull(room(["ann"], 2))).toBe(false);
    expect(isFull(room(["ann", "bob"], 2))).toBe(true);
  });

  test("the capacity floor is two, rising with the seated count", () => {
    expect(capacityFloor(room(["ann"]))).toBe(2);
    expect(capacityFloor(room(["ann", "bob", "cy"]))).toBe(3);
  });

  test("open seats are counted apart from the occupied ones", () => {
    expect(openSeats(room(["ann"], 8))).toBe(7);
    expect(openSeats(room(["ann"], 2))).toBe(1);
    expect(openSeats(room(["ann", "bob"], 2))).toBe(0);
  });

  test("the owner's tile reads host and anyone else's reads ready", () => {
    expect(seatState(room(["ann", "bob"]), "ann")).toBe("host");
    expect(seatState(room(["ann", "bob"]), "bob")).toBe("ready");
  });

  test("the roster draws three columns up to three seats, four beyond", () => {
    expect(rosterColumns(2)).toBe(3);
    expect(rosterColumns(3)).toBe(3);
    expect(rosterColumns(4)).toBe(4);
    expect(rosterColumns(8)).toBe(4);
  });
});
