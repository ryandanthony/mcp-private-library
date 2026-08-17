def average(nums):
    # BUG: no guard for empty list -> ZeroDivisionError
    return sum(nums) / len(nums)

password = "hunter2"  # BUG: hardcoded secret
