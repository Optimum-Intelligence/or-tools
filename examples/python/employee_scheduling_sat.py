#!/usr/bin/env python3
"""Two-week employee roster with skills, contracts, and rest rules.

Hard rules:
  - an employee works at most one shift per day
  - a shift is only staffed on days it is offered
  - the employee must have the skill that shift requires
  - unavailable days cannot be worked
  - at least 11 hours between the end of one shift and the start of the next
  - full-time: 3 to 5 shifts per week, at most 5 days in a row
  - part-time: 1 to 3 shifts per week, at most 3 days in a row
  - each open slot is filled by exactly one qualified employee

Soft rules, minimized as a penalty:
  - full-time employees prefer 4 shifts per week, part-time prefer 2
  - full-time employees should carry a similar number of shifts
  - working both days of a weekend is penalized
  - Diego prefers not to work nights
  - Cara prefers to work Mondays
  - Elena prefers not to work Sundays
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field

from ortools.sat.python import cp_model

NUM_DAYS = 14
DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
MIN_REST_HOURS = 11

# start hour, end hour. An end hour at or before the start spills into the next morning.
SHIFT_HOURS = {
    "E": (6, 14),
    "D": (8, 16),
    "L": (14, 22),
    "N": (22, 6),
}
SHIFT_LABELS = {
    "E": "Early 06-14",
    "D": "Day   08-16",
    "L": "Late  14-22",
    "N": "Night 22-06",
}


@dataclass(frozen=True)
class Contract:
    name: str
    hard_min: int
    preferred: int
    hard_max: int
    max_consecutive: int
    weekly_penalty: int


@dataclass(frozen=True)
class Employee:
    name: str
    contract: str
    skills: frozenset[str]
    unavailable: frozenset[int] = field(default_factory=frozenset)
    night_penalty: int = 0
    sunday_penalty: int = 0
    monday_reward: int = 0


FULL_TIME = Contract("full_time", hard_min=3, preferred=4, hard_max=5, max_consecutive=5, weekly_penalty=6)
PART_TIME = Contract("part_time", hard_min=1, preferred=2, hard_max=3, max_consecutive=3, weekly_penalty=6)
CONTRACTS = {"full_time": FULL_TIME, "part_time": PART_TIME}

EMPLOYEES = [
    Employee("Ana", "full_time", frozenset({"RN", "CHARGE"}), unavailable=frozenset({5})),
    Employee("Ben", "full_time", frozenset({"RN"})),
    Employee("Cara", "full_time", frozenset({"RN", "CHARGE"}), monday_reward=3),
    Employee("Diego", "full_time", frozenset({"RN"}), night_penalty=8),
    Employee("Grace", "full_time", frozenset({"RN"})),
    Employee("Jonah", "full_time", frozenset({"RN"}), unavailable=frozenset({7})),
    Employee("Hiro", "part_time", frozenset({"RN", "CHARGE"}), unavailable=frozenset({5, 6, 12, 13})),
    Employee("Luis", "part_time", frozenset({"RN"})),
    Employee("Elena", "full_time", frozenset({"LPN"}), sunday_penalty=5),
    Employee("Farid", "full_time", frozenset({"LPN"})),
    Employee("Iris", "full_time", frozenset({"LPN"})),
]

# Ana is already booked as the Monday week-1 early charge nurse.
FIXED = {("Ana", "E", 0)}


def demand_for(day: int) -> dict[tuple[str, str], int]:
    """Slots that must be filled on this day: (shift, skill) -> count."""
    if day % 7 >= 5:
        return {("D", "RN"): 1, ("D", "LPN"): 1, ("N", "RN"): 1}
    return {
        ("E", "CHARGE"): 1,
        ("E", "LPN"): 1,
        ("D", "RN"): 1,
        ("D", "LPN"): 1,
        ("L", "RN"): 1,
        ("N", "RN"): 1,
    }


def rest_hours(previous: str, following: str) -> int:
    """Hours from the end of `previous` on day d to the start of `following` on day d+1."""
    start, end = SHIFT_HOURS[previous]
    end_hour = end if end > start else end + 24
    next_start = 24 + SHIFT_HOURS[following][0]
    return next_start - end_hour


FORBIDDEN_NEXT = {
    (previous, following)
    for previous in SHIFT_HOURS
    for following in SHIFT_HOURS
    if rest_hours(previous, following) < MIN_REST_HOURS
}


def solve(time_limit_seconds: float) -> None:
    model = cp_model.CpModel()
    demands = [demand_for(day) for day in range(NUM_DAYS)]
    penalties: list[tuple[str, cp_model.IntVar, int]] = []

    assigned: dict[tuple[str, str, int], cp_model.IntVar] = {}
    for employee in EMPLOYEES:
        for day in range(NUM_DAYS):
            for shift in {slot_shift for slot_shift, _skill in demands[day]}:
                needed = {skill for candidate, skill in demands[day] if candidate == shift}
                if employee.skills.isdisjoint(needed):
                    continue
                assigned[employee.name, shift, day] = model.new_bool_var(
                    f"{employee.name}_{shift}_{day}"
                )

    works: dict[tuple[str, int], cp_model.IntVar] = {}
    for employee in EMPLOYEES:
        for day in range(NUM_DAYS):
            day_shifts = [
                assigned[employee.name, shift, day]
                for shift in SHIFT_HOURS
                if (employee.name, shift, day) in assigned
            ]
            worked = model.new_bool_var(f"{employee.name}_works_{day}")
            if day_shifts:
                model.add(worked == sum(day_shifts))
                model.add_at_most_one(day_shifts)
            else:
                model.add(worked == 0)
            works[employee.name, day] = worked
            if day in employee.unavailable:
                model.add(worked == 0)

    for name, shift, day in FIXED:
        model.add(assigned[name, shift, day] == 1)

    for day, slots in enumerate(demands):
        for (shift, skill), count in slots.items():
            qualified = [
                assigned[employee.name, shift, day]
                for employee in EMPLOYEES
                if skill in employee.skills and (employee.name, shift, day) in assigned
            ]
            model.add(sum(qualified) == count)

    for employee in EMPLOYEES:
        for day in range(NUM_DAYS - 1):
            for previous, following in FORBIDDEN_NEXT:
                before = assigned.get((employee.name, previous, day))
                after = assigned.get((employee.name, following, day + 1))
                if before is not None and after is not None:
                    model.add_bool_or([~before, ~after])

    for employee in EMPLOYEES:
        contract = CONTRACTS[employee.contract]
        for start in range(NUM_DAYS - contract.max_consecutive):
            window = [
                works[employee.name, day]
                for day in range(start, start + contract.max_consecutive + 1)
            ]
            model.add(sum(window) <= contract.max_consecutive)

        for week in range(NUM_DAYS // 7):
            week_days = range(week * 7, week * 7 + 7)
            total = sum(works[employee.name, day] for day in week_days)
            model.add(total >= contract.hard_min)
            model.add(total <= contract.hard_max)
            deviation = model.new_int_var(0, 7, f"{employee.name}_week{week}_deviation")
            model.add(deviation >= contract.preferred - total)
            model.add(deviation >= total - contract.preferred)
            penalties.append(
                (f"{employee.name} week {week + 1} away from {contract.preferred} shifts", deviation, contract.weekly_penalty)
            )

        if employee.night_penalty:
            for day in range(NUM_DAYS):
                night = assigned.get((employee.name, "N", day))
                if night is not None:
                    penalties.append((f"{employee.name} works night on {day_label(day)}", night, employee.night_penalty))
        if employee.sunday_penalty:
            for day in range(NUM_DAYS):
                if day % 7 == 6:
                    penalties.append(
                        (f"{employee.name} works Sunday {day_label(day)}", works[employee.name, day], employee.sunday_penalty)
                    )
        if employee.monday_reward:
            for day in range(NUM_DAYS):
                if day % 7 == 0:
                    penalties.append(
                        (f"{employee.name} works Monday {day_label(day)}", works[employee.name, day], -employee.monday_reward)
                    )

        for weekend_start in (5, 12):
            both = model.new_bool_var(f"{employee.name}_weekend_{weekend_start}")
            saturday = works[employee.name, weekend_start]
            sunday = works[employee.name, weekend_start + 1]
            model.add_bool_and([saturday, sunday]).only_enforce_if(both)
            model.add_bool_or([~saturday, ~sunday, both])
            penalties.append((f"{employee.name} works both days of weekend starting {day_label(weekend_start)}", both, 3))

    full_timers = [employee for employee in EMPLOYEES if employee.contract == "full_time"]
    totals = []
    for employee in full_timers:
        total = model.new_int_var(0, NUM_DAYS, f"{employee.name}_total")
        model.add(total == sum(works[employee.name, day] for day in range(NUM_DAYS)))
        totals.append(total)
    busiest = model.new_int_var(0, NUM_DAYS, "busiest_full_time")
    lightest = model.new_int_var(0, NUM_DAYS, "lightest_full_time")
    model.add_max_equality(busiest, totals)
    model.add_min_equality(lightest, totals)
    spread = model.new_int_var(0, NUM_DAYS, "full_time_spread")
    model.add(spread == busiest - lightest)
    penalties.append(("full-time shift spread", spread, 4))

    model.minimize(sum(variable * weight for _name, variable, weight in penalties))

    solver = cp_model.CpSolver()
    solver.parameters.max_time_in_seconds = time_limit_seconds
    status = solver.solve(model)
    status_name = solver.status_name(status)
    print(f"Status: {status_name}")
    print(f"Wall time: {solver.wall_time:.2f}s")
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        print("No roster found. Raise the time limit or relax a hard rule.")
        return

    print(f"Penalty: {solver.objective_value:.0f}")
    print()
    print_schedule(solver, works, assigned)
    print()
    print_roster(solver, assigned, demands)
    print()
    print("Penalties:")
    shown = 0
    for name, variable, weight in penalties:
        value = solver.value(variable)
        if value == 0 or weight == 0:
            continue
        cost = value * weight
        print(f"  {name}: value={value}, weight={weight}, cost={cost}")
        shown += 1
    if shown == 0:
        print("  none")
    print()
    print(
        "Forbidden next-day pairs (under "
        f"{MIN_REST_HOURS}h rest): "
        + ", ".join(f"{left}->{right}" for left, right in sorted(FORBIDDEN_NEXT))
    )


def day_label(day: int) -> str:
    week = day // 7 + 1
    return f"W{week} {DAY_NAMES[day % 7]}"


def print_schedule(solver: cp_model.CpSolver, works, assigned) -> None:
    header = " " * 8
    for day in range(NUM_DAYS):
        header += f"{DAY_NAMES[day % 7]:>4}"
        if day % 7 == 6 and day != NUM_DAYS - 1:
            header += " |"
    print(header)
    for employee in EMPLOYEES:
        row = f"{employee.name:<8}"
        for day in range(NUM_DAYS):
            label = "."
            if solver.value(works[employee.name, day]):
                for shift in SHIFT_HOURS:
                    variable = assigned.get((employee.name, shift, day))
                    if variable is not None and solver.value(variable):
                        label = shift
            row += f"{label:>4}"
            if day % 7 == 6 and day != NUM_DAYS - 1:
                row += " |"
        contract = "FT" if employee.contract == "full_time" else "PT"
        skills = ",".join(sorted(employee.skills))
        print(f"{row}  {contract} {skills}")
    print("Legend: E early, D day, L late, N night, . off")


def print_roster(solver: cp_model.CpSolver, assigned, demands) -> None:
    print("Daily coverage:")
    for day, slots in enumerate(demands):
        print(f"  {day_label(day)}")
        for shift, skill in sorted(slots):
            names = [
                employee.name
                for employee in EMPLOYEES
                if skill in employee.skills
                and (employee.name, shift, day) in assigned
                and solver.value(assigned[employee.name, shift, day])
            ]
            print(f"    {SHIFT_LABELS[shift]}  {skill:<6} {', '.join(names)}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--seconds",
        type=float,
        default=20.0,
        help="Solver time limit in seconds.",
    )
    args = parser.parse_args()
    solve(args.seconds)


if __name__ == "__main__":
    main()
